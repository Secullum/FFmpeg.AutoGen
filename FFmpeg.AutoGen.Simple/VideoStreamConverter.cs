using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

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

        private const AVPixelFormat TARGET_PIX_FORMAT = AVPixelFormat.AV_PIX_FMT_YUV420P;
        private readonly AVCodecID[] WMV_FORMATS = new AVCodecID[] { AVCodecID.AV_CODEC_ID_WMV1, AVCodecID.AV_CODEC_ID_WMV2, AVCodecID.AV_CODEC_ID_WMV3 };

        private Size _size;
        private string _outputName;
        private string _inputName;

        private VideoFrameConverter _videoConverter;
        private AudioFrameConverter _audioConverter;

        private AVFormatContext* _pOutputFmCtx;
        private AVFormatContext* _pInputFmtCtx;
        private StreamContext[] _StreamsContextArr;

        private AVFrame* _pReusableFrame;
        private AVPacket* _pReusablePacket;
        private List<int> _validStreamIndexes;
        private long _videoFrameCount = 0;

        public VideoStreamConverter(Size destSize, string inputName)
        {
            _size = destSize;
            _inputName = inputName;

            _validStreamIndexes = new List<int>();

            OpenInputFile();
        }

        public void Dispose()
        {
            if (_videoConverter != null)
            {
                _videoConverter.Dispose();
            }

            if (_audioConverter != null)
            {
                _audioConverter.Dispose();
            }

            for (int i = 0; i < _StreamsContextArr.Length; i++)
            {
                fixed (AVCodecContext** ptr = &_StreamsContextArr[i].DecoderContext)
                {
                    ffmpeg.avcodec_free_context(ptr);
                }

                if (_pOutputFmCtx != null && _StreamsContextArr.Length > i && _pOutputFmCtx->streams[i] != null && _StreamsContextArr[i].EncoderContext != null)
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

            var streamIndex = 0;

            OpenOutputFile(shouldResize);
            InitializeConverters(shouldResize);

            while (ffmpeg.av_read_frame(_pInputFmtCtx, _pReusablePacket) == 0)
            {
                streamIndex = _pReusablePacket->stream_index;

                if (!_validStreamIndexes.Contains(streamIndex))
                {
                    FreePacket();

                    continue;
                }

                ffmpeg.av_packet_rescale_ts(_pReusablePacket,
                    _pInputFmtCtx->streams[streamIndex]->time_base,
                    _StreamsContextArr[streamIndex].DecoderContext->time_base);

                var decoderCtx = _StreamsContextArr[streamIndex].DecoderContext;

                ffmpeg.avcodec_send_packet(decoderCtx, _pReusablePacket).ThrowExceptionIfError();

                while (ffmpeg.avcodec_receive_frame(decoderCtx, _pReusableFrame) == 0)
                {
                    if (_pInputFmtCtx->streams[streamIndex]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        if (shouldResize)
                        {
                            var converted = _videoConverter.Convert(*_pReusableFrame);
                            FreeFrame();
                            *_pReusableFrame = converted;
                        }

                        _pReusableFrame->pts = 1600 * _videoFrameCount++;

                        EncodeWriteFrame(streamIndex).ThrowExceptionIfError();
                    }
                    else
                    {
                        _audioConverter.Convert(_pReusableFrame, frame =>
                        {
                            FreeFrame();
                            *_pReusableFrame = frame;

                            return EncodeWriteFrame(streamIndex);
                        });
                    }

                    FreeFrame();
                }

                FreePacket();
            }

            for (var i = 0; i < _StreamsContextArr.Length; i++)
            {
                FlushEncoder(i);
            }

            ffmpeg.av_write_trailer(_pOutputFmCtx);
        }

        private void InitializeConverters(bool shouldResize)
        {
            var videoStreamIndex = Array.FindIndex(_StreamsContextArr, x => x.DecoderContext->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO);

            if (videoStreamIndex < 0)
            {
                throw new Exception("No video stream found");
            }

            if (shouldResize)
            {
                var decoderContext = _StreamsContextArr[videoStreamIndex].DecoderContext;

                _videoConverter = new VideoFrameConverter(new Size(decoderContext->width, decoderContext->height),
                    decoderContext->pix_fmt,
                    _size,
                    TARGET_PIX_FORMAT);
            }

            var audioStreamIndex = Array.FindIndex(_StreamsContextArr, x => x.DecoderContext->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO);

            if (audioStreamIndex > -1)
            {
                _audioConverter = new AudioFrameConverter(_StreamsContextArr[audioStreamIndex].EncoderContext,
                    _StreamsContextArr[audioStreamIndex].DecoderContext);
            }
        }

        private void ConfigureEncoderSpecificSettings(AVCodecContext* codecCtx)
        {
            if (codecCtx->codec_id == AVCodecID.AV_CODEC_ID_MSMPEG4V3)
            {
                codecCtx->max_b_frames = 0;
            }
        }

        private void OpenInputFile()
        {
            _pInputFmtCtx = null;

            fixed (AVFormatContext** inputFmtCtxPtr = &_pInputFmtCtx)
            {
                ffmpeg.avformat_open_input(inputFmtCtxPtr, _inputName, null, null).ThrowExceptionIfError();
            }

            ffmpeg.avformat_find_stream_info(_pInputFmtCtx, null).ThrowExceptionIfError();

            for (int i = 0; i < _pInputFmtCtx->nb_streams; i++)
            {
                if (_pInputFmtCtx->streams[i]->codecpar->codec_id != AVCodecID.AV_CODEC_ID_NONE)
                {
                    _validStreamIndexes.Add(i);
                }
            }

            _StreamsContextArr = new StreamContext[_validStreamIndexes.Count];

            foreach (var i in _validStreamIndexes)
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
                    else
                    {
                        var channelLayout = codecCtx->channel_layout;

                        if (channelLayout == 0)
                        {
                            channelLayout = (ulong)ffmpeg.av_get_default_channel_layout(codecCtx->channels);
                        }

                        codecCtx->channel_layout = channelLayout;
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

            foreach (var i in _validStreamIndexes)
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
                            num = 1,
                            den = 40000
                        };

                        encoderCtx->gop_size = 10;
                        encoderCtx->max_b_frames = 1;

                        ffmpeg.av_opt_set(encoderCtx->priv_data, "crf", "30", ffmpeg.AV_OPT_SEARCH_CHILDREN);
                        ffmpeg.av_opt_set(encoderCtx->priv_data, "preset", "veryfast", ffmpeg.AV_OPT_SEARCH_CHILDREN);

                        ConfigureEncoderSpecificSettings(encoderCtx);
                    }
                    else
                    {
                        encoderCtx->sample_rate = decoderCtx->sample_rate;
                        encoderCtx->channels = ffmpeg.av_get_channel_layout_nb_channels(ffmpeg.AV_CH_LAYOUT_STEREO);
                        encoderCtx->channel_layout = ffmpeg.AV_CH_LAYOUT_STEREO;
                        encoderCtx->sample_fmt = encoder->sample_fmts[0];

                        encoderCtx->time_base = new AVRational
                        {
                            num = 1,
                            den = encoderCtx->sample_rate
                        };
                    }

                    outStream->time_base = encoderCtx->time_base;

                    if ((_pOutputFmCtx->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                    {
                        encoderCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
                    }

                    ffmpeg.avcodec_open2(encoderCtx, encoder, null).ThrowExceptionIfError();

                    ffmpeg.avcodec_parameters_from_context(outStream->codecpar, encoderCtx).ThrowExceptionIfError();

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
            }

            AVDictionary* options;
            ffmpeg.av_dict_set(&options, "movflags", "faststart", 0);

            if ((_pOutputFmCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ffmpeg.avio_open2(&_pOutputFmCtx->pb, _outputName, ffmpeg.AVIO_FLAG_WRITE, null, &options).ThrowExceptionIfError();
            }

            ffmpeg.avformat_write_header(_pOutputFmCtx, null).ThrowExceptionIfError();
        }

        private int EncodeWriteFrame(int streamIndex, bool flush = false)
        {
            var ret = 0;

            var stream = _StreamsContextArr[streamIndex];

            var streamDecCtx = stream.DecoderContext;
            var streamEncCtx = stream.EncoderContext;
            var streamType = streamEncCtx->codec_type;

            ffmpeg.avcodec_send_frame(streamEncCtx, flush ? null : _pReusableFrame).ThrowExceptionIfError();

            FreeFrame();

            while ((ret = ffmpeg.avcodec_receive_packet(streamEncCtx, _pReusablePacket)) >= 0)
            {
                _pReusablePacket->stream_index = streamIndex;

                ffmpeg.av_packet_rescale_ts(_pReusablePacket,
                                     stream.EncoderContext->time_base,
                                     _pOutputFmCtx->streams[streamIndex]->time_base);

                ffmpeg.av_interleaved_write_frame(_pOutputFmCtx, _pReusablePacket).ThrowExceptionIfError();

                FreePacket();
            };

            if (flush)
            {
                ffmpeg.avcodec_flush_buffers(streamEncCtx);
            }
            else if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                return 0;
            }

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
                _pReusablePacket->data = null;
                _pReusablePacket->size = 0;
            }
        }

        private void FreeFrame(bool realloc = true)
        {
            for (uint i = 0; i < 8; i++)
            {
                _pReusableFrame->data[i] = null;
            }

            ffmpeg.av_frame_unref(_pReusableFrame);

            fixed (AVFrame** ptr = &_pReusableFrame)
            {
                ffmpeg.av_frame_free(ptr);
            }

            if (realloc)
            {
                _pReusableFrame = ffmpeg.av_frame_alloc();
                ffmpeg.av_frame_get_buffer(_pReusableFrame, 0);
            }
        }

        private void FlushEncoder(int streamIndex)
        {
            int ret = 0;
            var encoderCtx = _StreamsContextArr[streamIndex].EncoderContext;

            if ((encoderCtx->codec->capabilities & ffmpeg.AV_CODEC_CAP_DELAY) == 0)
            {
                return;
            }

            FreePacket();

            ret = EncodeWriteFrame(streamIndex, flush: true);

            if (ret == ffmpeg.AVERROR_EOF)
            {
                return;
            }

            ret.ThrowExceptionIfError();

            return;
        }

        private bool ArrPtrContains(int* arr, int value)
        {
            while (arr != null && *arr != -1) //-1 = terminador dos arrays de sample_fmts e pix_fmts do ffmpeg
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
