using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace FFmpeg.AutoGen.Simple
{
    public unsafe class VideoStreamConverter : IDisposable
    {
        public struct StreamContext
        {
            public AVCodecContext* DecoderContext;
            public AVCodecContext* EncoderContext;
        }

        public class StreamInfo
        {
            public Size Dimensions { get; set; }
            public AVMediaType MediaType { get; set; }
            public AVCodecID CodecId { get; set; }

            /// <summary>
            /// List of formats separeted by comma
            /// Eg.: mov,mp4,mpeg4
            /// </summary>
            public string FormatName { get; set; }
        }

        private avio_alloc_context_read_packet _readFunc;
        private avio_alloc_context_seek _seekFunc;

        const AVPixelFormat TARGET_PIX_FORMAT = AVPixelFormat.AV_PIX_FMT_YUV420P;

        private Size _size;
        private string _outputName;
        private string _inputName;
        private Stream _fileStream;

        private VideoFrameConverter _converter;

        private AVFormatContext* _pOutputFmCtx;
        private AVFormatContext* _pInputFmtCtx;
        private AVIOContext* _pIOCtx;
        private StreamContext[] _StreamsContextArr;

        private AVFrame* _pReusableFrame;
        private AVPacket* _pReusablePacket;
        private IntPtr _pInternalBuffer;
        private GCHandle _pStreamPtr;
        private int _frameCount = 0;

        public VideoStreamConverter(Size destSize, string inputName)
        {
            _size = destSize;
            _inputName = inputName;

            OpenInputFile();
        }

        public VideoStreamConverter(Size destSize, Stream stream)
        {
            _size = destSize;
            _fileStream = stream;

            OpenInputFile();
        }

        public void Dispose()
        {
            if (_converter != null)
            {
                _converter.Dispose();
            }

            _pStreamPtr.Free();

            Marshal.FreeHGlobal(_pInternalBuffer);

            for (int i = 0; i < _pInputFmtCtx->nb_streams; i++)
            {
                fixed (AVCodecContext** ptr = &_StreamsContextArr[i].DecoderContext)
                {
                    ffmpeg.avcodec_free_context(ptr);
                }

                if (_pOutputFmCtx != null && _pOutputFmCtx->nb_streams > i && _pOutputFmCtx->streams[i] != null && _StreamsContextArr[i].EncoderContext != null)
                {
                    fixed (AVCodecContext** ptr = &_StreamsContextArr[i].EncoderContext)
                    {
                        ffmpeg.avcodec_free_context(ptr);
                    }
                }
            }

            fixed (AVFormatContext** ptr = &_pInputFmtCtx)
            {
                ffmpeg.avformat_close_input(ptr);
            }

            if (_pOutputFmCtx != null && (_pOutputFmCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ffmpeg.avio_closep(&_pOutputFmCtx->pb);
            }

            fixed (AVIOContext** ptr = &_pIOCtx)
            {
                ffmpeg.avio_context_free(ptr);
            }

            ffmpeg.av_free(_pIOCtx);

            ffmpeg.avformat_free_context(_pOutputFmCtx);
            _pOutputFmCtx = null;

            ffmpeg.avformat_free_context(_pInputFmtCtx);
            _pInputFmtCtx = null;

            if (_pReusablePacket != null)
            {
                FreePacket(realloc: false);
            }

            if (_pReusableFrame != null)
            {
                FreeFrame(realloc: false);
            }
        }

        public List<StreamInfo> GetInputFileInfo()
        {
            return _StreamsContextArr.Select(x => new StreamInfo()
            {
                CodecId = x.DecoderContext->codec_id,
                FormatName = new string((sbyte*)_pInputFmtCtx->iformat->name),
                Dimensions = new Size(x.DecoderContext->width, x.DecoderContext->height),
                MediaType = x.DecoderContext->codec_type
            }).ToList();
        }

        public void Convert(string outputName, bool shouldResize)
        {
            _outputName = outputName;
            _pReusablePacket = ffmpeg.av_packet_alloc();
            _pReusableFrame = ffmpeg.av_frame_alloc();

            var mediaType = AVMediaType.AVMEDIA_TYPE_UNKNOWN;
            var gotResult = 0;
            var streamIndex = 0;
            var ret = 0;

            OpenOutputFile(shouldResize);

            if (shouldResize)
            {
                InitializeConverter();
            }

            while (ffmpeg.av_read_frame(_pInputFmtCtx, _pReusablePacket) == 0)
            {
                streamIndex = _pReusablePacket->stream_index;

                var decoderCtx = _StreamsContextArr[streamIndex].DecoderContext;
                var inputStream = _pInputFmtCtx->streams[streamIndex];
                mediaType = inputStream->codecpar->codec_type;

                ffmpeg.av_packet_rescale_ts(_pReusablePacket,
                    inputStream->time_base,
                    decoderCtx->time_base);

                ret = mediaType == AVMediaType.AVMEDIA_TYPE_VIDEO ?
                    ffmpeg.avcodec_decode_video2(decoderCtx, _pReusableFrame, &gotResult, _pReusablePacket) :
                    ffmpeg.avcodec_decode_audio4(decoderCtx, _pReusableFrame, &gotResult, _pReusablePacket);

                if (ret < 0)
                {
                    throw new Exception("Decoding error");
                }
                else if (gotResult != 0)
                {
                    if (mediaType == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        if (shouldResize)
                        {
                            *_pReusableFrame = _converter.Convert(*_pReusableFrame);
                        }
                    }

                    _pReusableFrame->pts = _frameCount++;

                    EncodeWriteFrame(streamIndex, &gotResult).ThrowExceptionIfError();
                }

                FreeFrame();
                FreePacket();
            }

            for (var i = 0; i < _StreamsContextArr.Length; i++)
            {
                FlushEncoder(i).ThrowExceptionIfError();
            }

            ffmpeg.av_write_trailer(_pOutputFmCtx);
        }

        private void InitializeConverter()
        {
            var videoStreamIndex = Array.FindIndex(_StreamsContextArr, x => x.DecoderContext->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO);

            if (videoStreamIndex < 0)
            {
                throw new Exception("No video stream found");
            }

            var decoderContext = _StreamsContextArr[videoStreamIndex].DecoderContext;

            _converter = new VideoFrameConverter(new Size(decoderContext->width, decoderContext->height), decoderContext->pix_fmt, _size, TARGET_PIX_FORMAT);
        }

        private void ConfigureEncoderSpecificSettings(AVCodecContext* codecCtx)
        {
            if (codecCtx->codec_id == AVCodecID.AV_CODEC_ID_MSMPEG4V3)
            {
                codecCtx->max_b_frames = 0;
            }
        }

        private AVFormatContext* CreateCustomIOContext()
        {
            _readFunc = new avio_alloc_context_read_packet((void* opaque, byte* buf, int buf_size) =>
            {
                try
                {
                    for (var i = 0; i < buf_size; i++)
                    {
                        var ret = _fileStream.ReadByte();

                        if (ret < 0)
                        {
                            return ffmpeg.AVERROR_EOF;
                        }

                        *(buf + i) = (byte)ret;
                    }

                    return buf_size;
                }
                catch (Exception)
                {
                    return -1;
                }
            });

            _seekFunc = new avio_alloc_context_seek((void* opaque, long offset, int whence) =>
            {
                if (whence == ffmpeg.AVSEEK_SIZE)
                {
                    return _fileStream.Length;
                }

                return _fileStream.Seek(offset, (SeekOrigin)whence);
            });

            var bufferSize = 32 * 1024;
            _pInternalBuffer = Marshal.AllocHGlobal(bufferSize);

            _pIOCtx = ffmpeg.avio_alloc_context((byte*)_pInternalBuffer, bufferSize, 0, null, _readFunc, null, _seekFunc);

            var pCtx = ffmpeg.avformat_alloc_context();
            pCtx->pb = _pIOCtx;

            CopyToBytePtr(_fileStream, (byte*)_pInternalBuffer, bufferSize);

            var emptyBytePtr = ffmpeg.av_malloc(1);

            var probeData = new AVProbeData
            {
                buf = (byte*)_pInternalBuffer,
                buf_size = bufferSize,
                filename = (byte*)emptyBytePtr,
            };

            pCtx->probesize = 120 * 1000;
            pCtx->iformat = ffmpeg.av_probe_input_format(&probeData, 1);
            pCtx->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;

            ffmpeg.av_free(emptyBytePtr);

            return pCtx;
        }

        private void OpenInputFile()
        {
            var isStream = _fileStream != null;

            _pInputFmtCtx = isStream ? CreateCustomIOContext() : null;

            fixed (AVFormatContext** inputFmtCtxPtr = &_pInputFmtCtx)
            {
                ffmpeg.avformat_open_input(inputFmtCtxPtr, _inputName, null, null).ThrowExceptionIfError();
            }

            ffmpeg.avformat_find_stream_info(_pInputFmtCtx, null).ThrowExceptionIfError();

            _StreamsContextArr = new StreamContext[_pInputFmtCtx->nb_streams];

            for (var i = 0; i < _pInputFmtCtx->nb_streams; i++)
            {
                var stream = _pInputFmtCtx->streams[i];
                var decoder = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);

                if (decoder == null)
                {
                    throw new Exception("Decoder not found");
                }

                var codecCtx = ffmpeg.avcodec_alloc_context3(decoder);

                if (codecCtx == null)
                {
                    throw new Exception("Failed to allocate the decoder context for stream");
                }

                ffmpeg.avcodec_parameters_to_context(codecCtx, stream->codecpar).ThrowExceptionIfError();

                if (codecCtx->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO || codecCtx->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    if (codecCtx->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        codecCtx->framerate = ffmpeg.av_guess_frame_rate(_pInputFmtCtx, stream, null);
                    }

                    ffmpeg.avcodec_open2(codecCtx, decoder, null).ThrowExceptionIfError();
                }
                _StreamsContextArr[i].DecoderContext = codecCtx;
            }
        }

        private void OpenOutputFile(bool shouldResize)
        {
            AVStream* outStream;
            AVStream* inStream;
            AVCodecContext* decoderCtx, encoderCtx;
            AVCodec* encoder;

            _pOutputFmCtx = null;

            fixed (AVFormatContext** outFmtCtxPtr = &_pOutputFmCtx)
            {
                ffmpeg.avformat_alloc_output_context2(outFmtCtxPtr, null, null, _outputName).ThrowExceptionIfError();
            }

            for (var i = 0; i < _pInputFmtCtx->nb_streams; i++)
            {
                outStream = ffmpeg.avformat_new_stream(_pOutputFmCtx, null);
                if (outStream == null)
                {
                    throw new Exception("Failed to allocate output stream");
                }

                inStream = _pInputFmtCtx->streams[i];
                decoderCtx = _StreamsContextArr[i].DecoderContext;

                var codecType = decoderCtx->codec_type;

                if (codecType == AVMediaType.AVMEDIA_TYPE_VIDEO || codecType == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    if (codecType == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        var format = ffmpeg.av_guess_format(null, _outputName, null);

                        encoder = ffmpeg.avcodec_find_encoder(format->video_codec);
                    }
                    else
                    {
                        encoder = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
                    }

                    if (encoder == null)
                    {
                        throw new Exception("Necessary encoder not found");
                    }

                    encoderCtx = ffmpeg.avcodec_alloc_context3(encoder);
                    if (encoderCtx == null)
                    {
                        throw new Exception("Failed to allocate the encoder context");
                    }

                    if (decoderCtx->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        encoderCtx->height = shouldResize ? _size.Height : decoderCtx->height;
                        encoderCtx->width = shouldResize ? _size.Width : decoderCtx->width;
                        encoderCtx->bit_rate = decoderCtx->bit_rate;
                        encoderCtx->framerate = new AVRational()
                        {
                            num = decoderCtx->framerate.den,
                            den = decoderCtx->framerate.num
                        };

                        if (encoder->pix_fmts != null)
                        {
                            if (ArrPtrContains((int*)encoder->pix_fmts, (int)TARGET_PIX_FORMAT))
                            {
                                encoderCtx->pix_fmt = TARGET_PIX_FORMAT;
                            }
                            else
                            {
                                encoderCtx->pix_fmt = encoder->pix_fmts[0];
                            }
                        }
                        else
                        {
                            encoderCtx->pix_fmt = decoderCtx->pix_fmt;
                        }

                        encoderCtx->time_base = new AVRational
                        {
                            num = decoderCtx->framerate.den,
                            den = decoderCtx->framerate.num
                        };

                        ffmpeg.av_opt_set(encoderCtx->priv_data, "crf", "30", ffmpeg.AV_OPT_SEARCH_CHILDREN);
                        ffmpeg.av_opt_set(encoderCtx->priv_data, "preset", "veryfast", ffmpeg.AV_OPT_SEARCH_CHILDREN);
                    }
                    else
                    {
                        var chnlLayout = decoderCtx->channel_layout;

                        if (chnlLayout == 0)
                        {
                            chnlLayout = (ulong)ffmpeg.av_get_default_channel_layout(decoderCtx->channels);
                        }

                        encoderCtx->sample_rate = decoderCtx->sample_rate;
                        encoderCtx->bit_rate = 48 * 2000;
                        encoderCtx->channel_layout = chnlLayout;
                        encoderCtx->channels = ffmpeg.av_get_channel_layout_nb_channels(chnlLayout);
                        encoderCtx->sample_fmt = encoder->sample_fmts[0];
                        encoderCtx->time_base = new AVRational
                        {
                            num = 1,
                            den = encoderCtx->sample_rate
                        };
                    }

                    ConfigureEncoderSpecificSettings(encoderCtx);

                    if ((_pOutputFmCtx->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                    {
                        encoderCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
                    }

                    ffmpeg.avcodec_open2(encoderCtx, encoder, null).ThrowExceptionIfError();

                    ffmpeg.avcodec_parameters_from_context(outStream->codecpar, encoderCtx).ThrowExceptionIfError();

                    outStream->time_base = encoderCtx->time_base;
                    _StreamsContextArr[i].EncoderContext = encoderCtx;
                }
                else if (decoderCtx->codec_type == AVMediaType.AVMEDIA_TYPE_UNKNOWN)
                {
                    throw new Exception("Invalid stream type");
                }
                else
                {
                    ffmpeg.avcodec_parameters_copy(outStream->codecpar, inStream->codecpar).ThrowExceptionIfError();

                    outStream->time_base = inStream->time_base;
                }

                encoderCtx = null;
                encoder = null;
            }

            AVDictionary* options;
            ffmpeg.av_dict_set(&options, "movflags", "faststart", 0);

            if ((_pOutputFmCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ffmpeg.avio_open2(&_pOutputFmCtx->pb, _outputName, ffmpeg.AVIO_FLAG_WRITE, null, &options).ThrowExceptionIfError();
            }

            ffmpeg.avformat_write_header(_pOutputFmCtx, null).ThrowExceptionIfError();
        }

        private int EncodeWriteFrame(int streamIndex, int* gotFrame, bool flush = false)
        {
            var ret = 0;
            var gotPacket = 0;

            if (gotFrame != null && *gotFrame == 0)
            {
                gotFrame = &gotPacket;
            }

            _pReusablePacket->data = null;
            _pReusablePacket->size = 0;

            var stream = _StreamsContextArr[streamIndex];

            var streamEncCtx = stream.EncoderContext;
            var streamType = streamEncCtx->codec_type;

            ret = streamType == AVMediaType.AVMEDIA_TYPE_VIDEO ?
                ffmpeg.avcodec_encode_video2(streamEncCtx, _pReusablePacket, flush ? null : _pReusableFrame, &gotPacket) :
                ffmpeg.avcodec_encode_audio2(streamEncCtx, _pReusablePacket, flush ? null : _pReusableFrame, &gotPacket);

            if (ret < 0)
            {
                FreePacket(realloc: false);
                FreeFrame(realloc: false);

                return ret;
            }

            if (gotPacket == 0)
            {
                FreeFrame();

                return 0;
            }

            _pReusablePacket->stream_index = streamIndex;

            ffmpeg.av_packet_rescale_ts(_pReusablePacket,
                                 streamEncCtx->time_base,
                                 _pOutputFmCtx->streams[streamIndex]->time_base);

            ret = ffmpeg.av_interleaved_write_frame(_pOutputFmCtx, _pReusablePacket);

            FreePacket();
            FreeFrame();

            return ret;
        }

        private void FreePacket(bool realloc = true)
        {
            ffmpeg.av_packet_unref(_pReusablePacket);

            fixed (AVPacket** ptr = &_pReusablePacket)
            {
                ffmpeg.av_packet_free(ptr);
            }

            if (realloc)
            {
                _pReusablePacket = ffmpeg.av_packet_alloc();
            }
        }

        private void FreeFrame(bool realloc = true)
        {
            ffmpeg.av_frame_unref(_pReusableFrame);

            fixed (AVFrame** ptr = &_pReusableFrame)
            {
                ffmpeg.av_frame_free(ptr);
            }

            if (realloc)
            {
                _pReusableFrame = ffmpeg.av_frame_alloc();
            }
        }

        private int FlushEncoder(int stream_index)
        {
            int ret, gotFrame;

            if ((_StreamsContextArr[stream_index].EncoderContext->codec->capabilities & ffmpeg.AV_CODEC_CAP_DELAY) == 0)
            {
                return 0;
            }

            while (true)
            {
                ret = EncodeWriteFrame(stream_index, &gotFrame, flush: true);

                if (ret < 0)
                {
                    break;
                }
                else if (gotFrame == 0)
                {
                    return 0;
                }
            }

            return ret;
        }

        private bool ArrPtrContains(int* arr, int value)
        {
            while (arr != null && *arr != -1)
            {
                if (*arr == value)
                {
                    return true;
                }

                arr++;
            }

            return false;
        }

        private void CopyToBytePtr(Stream data, byte* buffer, int bufferLen)
        {
            for (var i = 0; i < bufferLen; i++)
            {
                var ret = data.ReadByte();

                if (ret < 0)
                {
                    return;
                }

                *(buffer + i) = (byte)ret;
            }

            data.Position = 0;
        }
    }
}
