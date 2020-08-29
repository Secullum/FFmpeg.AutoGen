using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
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
            public Size Dimensions { get; }
            public AVMediaType MediaType { get; }
            public AVCodecID CodecId { get; }
            public string CodecName { get; }

            public StreamInfo(Size dimensions, AVCodecID codecId, string codecName, AVMediaType mediaType)
            {
                Dimensions = dimensions;
                CodecId = codecId;
                CodecName = codecName;
                MediaType = mediaType;
            }
        }

        const AVPixelFormat PIX_FORMAT = AVPixelFormat.AV_PIX_FMT_YUV420P;

        private Size _size;
        private string _outputName;
        private string _inputName;
        private AVCodecID? _codec;

        private Stream _fileStream;

        private int _frameCount = 0;

        private VideoFrameConverter _converter;

        private AVFormatContext* _pOutputFmCtx;
        private AVFormatContext* _pInputFmtCtx;
        private AVIOContext* _pIOCtx;

        private StreamContext[] streamContextArr;

        private byte* _pInternalBuffer;
        private static AVPacket* _pReusablePacket;

        /// <summary>
        /// Classe para converter e redimensionar vídeos.
        /// Se destCodec não for passado, o codec de output será identificado com base na extensão do arquivo output
        /// </summary>
        /// <param name="destSize"></param>
        /// <param name="inputName"></param>
        /// <param name="destCodec"></param>
        public VideoStreamConverter(Size destSize, string inputName, AVCodecID? destCodec = null)
        {
            _size = destSize;
            _inputName = inputName;
            _codec = destCodec;

            OpenInputFile();
        }

        public VideoStreamConverter(Size destSize, Stream stream, string inputName)
        {
            _size = destSize;
            _fileStream = stream;
            _inputName = inputName;

            ffmpeg.av_register_all();

            OpenInputFile();
        }

        public List<StreamInfo> GetInputFileInfo()
        {
            return streamContextArr.Select(x => new StreamInfo(new Size(x.DecoderContext->width, x.DecoderContext->height),
                x.DecoderContext->codec_id,
                ffmpeg.avcodec_get_name(x.DecoderContext->codec_id),
                x.DecoderContext->codec_type)).ToList();
        }

        public void Convert(string outputName)
        {
            _outputName = outputName;

            var streamIndex = 0;
            var mediaType = AVMediaType.AVMEDIA_TYPE_UNKNOWN;
            var packet = new AVPacket
            {
                data = null,
                size = 0
            };

            AVFrame* frame = null;

            int gotResult = 0;

            OpenOutputFile();
            InitializeConverter();

            while (ffmpeg.av_read_frame(_pInputFmtCtx, &packet) == 0)
            {
                streamIndex = packet.stream_index;

                var inputStream = _pInputFmtCtx->streams[streamIndex];
                mediaType = inputStream->codecpar->codec_type;

                if (mediaType == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    frame = ffmpeg.av_frame_alloc();

                    var decCtx = streamContextArr[streamIndex].DecoderContext;

                    ffmpeg.av_packet_rescale_ts(&packet,
                        inputStream->time_base,
                        decCtx->time_base);

                    ffmpeg.avcodec_decode_video2(decCtx, frame, &gotResult, &packet);

                    if (gotResult != 0)
                    {
                        var converted = _converter.Convert(*frame);
                        ffmpeg.av_frame_copy_props(&converted, frame);

                        converted.pts = _frameCount++;

                        EncodeWriteFrame(&converted, streamIndex);
                    }
                }
                else
                {
                    ffmpeg.av_packet_rescale_ts(&packet,
                        inputStream->time_base,
                        _pOutputFmCtx->streams[streamIndex]->time_base);

                    ffmpeg.av_interleaved_write_frame(_pOutputFmCtx, &packet).ThrowExceptionIfError();
                }
            }

            var videoStreamsCount = streamContextArr.Count(x => x.DecoderContext->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO);

            for (var i = 0; i < videoStreamsCount; i++)
            {
                FlushEncoder(i).ThrowExceptionIfError();
            }

            ffmpeg.av_write_trailer(_pOutputFmCtx);

            //Free pointers
            ffmpeg.av_frame_unref(frame);
            ffmpeg.av_frame_free(&frame);

            var packetPtr = &packet;
            ffmpeg.av_packet_unref(packetPtr);
            ffmpeg.av_packet_free(&packetPtr);
        }

        public void Dispose()
        {
            _converter.Dispose();

            for (int i = 0; i < _pInputFmtCtx->nb_streams; i++)
            {
                var decoderCtx = streamContextArr[i].DecoderContext;
                ffmpeg.avcodec_free_context(&decoderCtx);

                if (_pOutputFmCtx != null && _pOutputFmCtx->nb_streams > i && _pOutputFmCtx->streams[i] != null && streamContextArr[i].EncoderContext != null)
                {
                    var encoderCtx = streamContextArr[i].EncoderContext;
                    ffmpeg.avcodec_free_context(&encoderCtx);
                }
            }

            var inputFmtCtx = _pInputFmtCtx;
            ffmpeg.avformat_close_input(&inputFmtCtx);

            ffmpeg.av_free(_pIOCtx);

            if (_pOutputFmCtx != null && (_pOutputFmCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ffmpeg.avio_closep(&_pOutputFmCtx->pb);
            }

            ffmpeg.avformat_free_context(_pOutputFmCtx);

            ffmpeg.av_free(_pInternalBuffer);
            ffmpeg.av_packet_unref(_pReusablePacket);
            var packetPtr = _pReusablePacket;
            ffmpeg.av_packet_free(&packetPtr);
        }

        private void InitializeConverter()
        {
            var videoStreamIndex = Array.FindIndex(streamContextArr, x => x.DecoderContext->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO);

            if (videoStreamIndex < 0)
            {
                throw new Exception("No video stream found");
            }

            var decoderContext = streamContextArr[videoStreamIndex].DecoderContext;

            _converter = new VideoFrameConverter(new Size(decoderContext->width, decoderContext->height), decoderContext->pix_fmt, _size, PIX_FORMAT);
        }

        private void ConfigureEncoderSpecificSettings(AVCodecContext* codecCtx)
        {
            if (codecCtx->codec_id == AVCodecID.AV_CODEC_ID_MSMPEG4V3)
            {
                codecCtx->max_b_frames = 0;
            }
        }

        public AVFormatContext* CreateCustomIOContext()
        {
            avio_alloc_context_read_packet ReadFunc = (void* opaque, byte* buf, int buf_size) =>
            {
                var safeBuffer = new byte[buf_size];

                try
                {
                    var readBytes = _fileStream.Read(safeBuffer, 0, buf_size);

                    Marshal.Copy(safeBuffer, 0, (IntPtr)buf, buf_size);

                    if (readBytes < 0)
                    {
                        return ffmpeg.AVERROR_EOF;
                    }

                    return readBytes;
                }
                catch (Exception)
                {
                    return -1;
                }
            };

            avio_alloc_context_seek SeekFunc = (void* opaque, long offset, int whence) =>
            {
                if (whence == ffmpeg.AVSEEK_SIZE)
                {
                    return _fileStream.Length;
                }

                return _fileStream.Seek(offset, (SeekOrigin)whence);
            };

            _pIOCtx = null;

            var bufferSize = 128 * 1024;
            _pInternalBuffer = (byte*)ffmpeg.av_mallocz((ulong)bufferSize);

            var dataLen = (int)_fileStream.Length;
            var dataArr = new byte[dataLen];
            _fileStream.Read(dataArr, 0, dataLen);
            _fileStream.Seek(0, SeekOrigin.Begin);

            fixed (byte* dataPtr = &dataArr[0])
            {
                _pIOCtx = ffmpeg.avio_alloc_context(_pInternalBuffer, bufferSize, 0, dataPtr, ReadFunc, null, SeekFunc);
            }

            var pCtx = ffmpeg.avformat_alloc_context();
            pCtx->pb = _pIOCtx;

            CopyToBytePtr(_fileStream, _pInternalBuffer, bufferSize);

            var emptyBytePtr = (byte*)ffmpeg.av_malloc(1);

            var probeData = new AVProbeData
            {
                buf = _pInternalBuffer,
                buf_size = bufferSize,
                filename = emptyBytePtr
            };

            pCtx->iformat = ffmpeg.av_probe_input_format(&probeData, 1);
            pCtx->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;

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

            streamContextArr = new StreamContext[_pInputFmtCtx->nb_streams];

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
                streamContextArr[i].DecoderContext = codecCtx;
            }
        }

        public void OpenOutputFile()
        {
            AVStream* outStream;
            AVStream* inStream;
            AVCodecContext* decoderCtx, encoderCtx;
            AVCodec* encoder;

            _pOutputFmCtx = null;

            fixed (AVFormatContext** outFmtCtxPtr = &_pOutputFmCtx)
            {
                ffmpeg.avformat_alloc_output_context2(outFmtCtxPtr, null, null, _outputName);
            }

            for (var i = 0; i < _pInputFmtCtx->nb_streams; i++)
            {
                outStream = ffmpeg.avformat_new_stream(_pOutputFmCtx, null);
                if (outStream == null)
                {
                    throw new Exception("Failed to allocate output stream");
                }

                inStream = _pInputFmtCtx->streams[i];
                decoderCtx = streamContextArr[i].DecoderContext;

                var codecType = decoderCtx->codec_type;

                if (codecType == AVMediaType.AVMEDIA_TYPE_VIDEO || codecType == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    if (codecType == AVMediaType.AVMEDIA_TYPE_VIDEO && _codec == null)
                    {
                        var format = ffmpeg.av_guess_format(null, _outputName, null);

                        encoder = ffmpeg.avcodec_find_encoder(format->video_codec);
                    }
                    else
                    {
                        encoder = ffmpeg.avcodec_find_encoder(codecType == AVMediaType.AVMEDIA_TYPE_VIDEO ? _codec.Value : decoderCtx->codec_id);
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
                        encoderCtx->height = _size.Height;
                        encoderCtx->width = _size.Width;
                        encoderCtx->bit_rate = decoderCtx->bit_rate;
                        encoderCtx->framerate = new AVRational()
                        {
                            num = decoderCtx->framerate.den,
                            den = decoderCtx->framerate.num
                        };

                        if (encoder->pix_fmts != null)
                        {
                            if (ArrPtrContains((int*)encoder->pix_fmts, (int)PIX_FORMAT))
                            {
                                encoderCtx->pix_fmt = PIX_FORMAT;
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

                        ConfigureEncoderSpecificSettings(encoderCtx);
                    }
                    else
                    {
                        encoderCtx->sample_rate = decoderCtx->sample_rate;
                        encoderCtx->channel_layout = decoderCtx->channel_layout;
                        encoderCtx->channels = ffmpeg.av_get_channel_layout_nb_channels(encoderCtx->channel_layout);
                        encoderCtx->sample_fmt = encoder->sample_fmts[0];
                        encoderCtx->time_base = new AVRational
                        {
                            num = 1,
                            den = encoderCtx->sample_rate
                        };
                    }

                    if ((_pOutputFmCtx->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                    {
                        encoderCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
                    }

                    ffmpeg.avcodec_open2(encoderCtx, encoder, null).ThrowExceptionIfError();

                    ffmpeg.avcodec_parameters_from_context(outStream->codecpar, encoderCtx).ThrowExceptionIfError();

                    outStream->time_base = encoderCtx->time_base;
                    streamContextArr[i].EncoderContext = encoderCtx;
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
            }

            AVDictionary* options = null;
            ffmpeg.av_dict_set(&options, "movflags", "faststart", 0);

            if ((_pOutputFmCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ffmpeg.avio_open2(&_pOutputFmCtx->pb, _outputName, ffmpeg.AVIO_FLAG_WRITE, null, &options).ThrowExceptionIfError();
            }

            ffmpeg.avformat_write_header(_pOutputFmCtx, null).ThrowExceptionIfError();
        }

        private int EncodeWriteFrame(AVFrame* filteredFrame, int streamIndex, int* gotFrame = null)
        {
            var ret = 0;
            var gotFrameLocal = 0;
            _pReusablePacket = ffmpeg.av_packet_alloc();

            if (gotFrame != null && *gotFrame == 0)
            {
                gotFrame = &gotFrameLocal;
            }

            ffmpeg.av_init_packet(_pReusablePacket);
            _pReusablePacket->data = null;
            _pReusablePacket->size = 0;

            var streamEncCtx = streamContextArr[streamIndex].EncoderContext;

            ret = ffmpeg.avcodec_encode_video2(streamEncCtx, _pReusablePacket, filteredFrame, &gotFrameLocal);

            if (ret < 0)
            {
                return ret;
            }

            if (gotFrameLocal == 0)
            {
                return 0;
            }

            _pReusablePacket->stream_index = streamIndex;

            ffmpeg.av_packet_rescale_ts(_pReusablePacket,
                                 streamEncCtx->time_base,
                                 _pOutputFmCtx->streams[streamIndex]->time_base);

            ret = ffmpeg.av_interleaved_write_frame(_pOutputFmCtx, _pReusablePacket);

            return ret;
        }

        private int FlushEncoder(int stream_index)
        {
            int ret, gotFrame;

            if ((streamContextArr[stream_index].EncoderContext->codec->capabilities & ffmpeg.AV_CODEC_CAP_DELAY) == 0)
                return 0;

            while (true)
            {
                ret = EncodeWriteFrame(null, stream_index, &gotFrame);

                if (ret < 0)
                    break;

                if (gotFrame == 0)
                    return 0;
            }

            return ret;
        }

        private bool ArrPtrContains(int* arr, int value)
        {
            if (arr == null)
            {
                return false;
            }

            var curr = 0;

            while (true)
            {
                curr = *(arr + curr);

                if (curr == -1)
                {
                    return false;
                }

                if (curr == value)
                {
                    return true;
                }
            }
        }

        private void CopyToBytePtr(Stream data, byte* buffer, int bufferLen)
        {
            var buf = new byte[bufferLen];

            data.Read(buf, 0, bufferLen);
            data.Position = 0;

            Marshal.Copy(buf, 0, (IntPtr)buffer, bufferLen);

            buf = null;
        }
    }
}
