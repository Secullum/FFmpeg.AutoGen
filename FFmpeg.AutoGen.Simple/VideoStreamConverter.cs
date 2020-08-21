using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace FFmpeg.AutoGen.Simple
{
    public unsafe class VideoStreamConverter
    {
        private Size _size;
        private int _bitRate;
        private int _fps;
        private string _name;
        private AVCodecID _codec;

        //private AVFormatContext* _pDestContext;

        public unsafe VideoStreamConverter(Size destSize, AVCodecID destCodec, int destBitrate, int destFps, string destName)
        {
            _size = destSize;
            _bitRate = destBitrate;
            _fps = destFps;
            _name = destName;
            _codec = destCodec;
        }

        public void Convert()
        {
            var outputFormat = ffmpeg.av_guess_format(null, _name, null);

            if (outputFormat == null)
            {
                throw new ApplicationException("Could not locate destiny format");
            }

            AVFormatContext* _pDestContext = null;

            ffmpeg.avformat_alloc_output_context2(&_pDestContext, outputFormat, null, _name);

            var codec = ffmpeg.avcodec_find_encoder(_codec);

            var newStream = ffmpeg.avformat_new_stream(_pDestContext, codec);

            var destCodecContext = ffmpeg.avcodec_alloc_context3(codec);

            newStream->codecpar->codec_id = outputFormat->video_codec;
            newStream->codecpar->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            newStream->codecpar->width = _size.Width;
            newStream->codecpar->height = _size.Height;
            newStream->codecpar->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
            newStream->codecpar->bit_rate = _bitRate * 1000;
            ffmpeg.avcodec_parameters_to_context(destCodecContext, newStream->codecpar);

            destCodecContext->time_base = new AVRational()
            {
                den = 1,
                num = 1
            };
            destCodecContext->max_b_frames = 2;
            destCodecContext->gop_size = 12;
            destCodecContext->framerate = new AVRational()
            {
                den = _fps,
                num = 1
            };

            if (newStream->codecpar->codec_id == AVCodecID.AV_CODEC_ID_H264)
            {
                ffmpeg.av_opt_set(destCodecContext, "preset", "ultrafast", 0);
            }

            ffmpeg.avcodec_parameters_from_context(newStream->codecpar, destCodecContext);

            ffmpeg.avcodec_open2(destCodecContext, codec, null).ThrowExceptionIfError();

            //if ((outputFormat->flags & ffmpeg.AVFMT_NOFILE) == ffmpeg.AVFMT_NOFILE)
            //{
                ffmpeg.avio_open(&_pDestContext->pb, _name, ffmpeg.AVIO_FLAG_WRITE).ThrowExceptionIfError();
            //}

            ffmpeg.avformat_write_header(_pDestContext, null).ThrowExceptionIfError();

            ffmpeg.av_dump_format(_pDestContext, 0, _name, 1);
        }
    }
}
