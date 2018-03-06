using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace FFmpeg.AutoGen.Simple
{
    public unsafe class VideoStreamDecoder : IDisposable
    {
        private bool _connected = false;
        private DateTime _initDate;
        private AVFormatContext* _pFormatContext;
        private AVCodecContext* _pCodecContext;
        private int _streamIndex;
        private AVFrame* _pFrame;
        private AVPacket* _pPacket;
        private AVIOInterruptCB_callback _interruptCallbackDelegate;
        private VideoFrameConverter _videoFrameConverter;

        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

        static VideoStreamDecoder()
        {
            FFmpegBinariesHelper.RegisterFFmpegBinaries();

            ffmpeg.av_register_all();
            ffmpeg.avcodec_register_all();
            ffmpeg.avformat_network_init();
        }

        public VideoStreamDecoder()
        {
            _interruptCallbackDelegate = new AVIOInterruptCB_callback(InterruptCallback);
        }
        
        public void Start(string url)
        {
            _pFormatContext = ffmpeg.avformat_alloc_context();

            _pFormatContext->interrupt_callback = new AVIOInterruptCB()
            {
                callback = new AVIOInterruptCB_callback_func()
                {
                    Pointer = Marshal.GetFunctionPointerForDelegate(_interruptCallbackDelegate)
                }
            };

            var pFormatContext = _pFormatContext;
            _initDate = DateTime.Now;
            _connected = false;
            ffmpeg.avformat_open_input(&pFormatContext, url, null, null).ThrowExceptionIfError();
            _connected = true;

            ffmpeg.avformat_find_stream_info(_pFormatContext, null).ThrowExceptionIfError();

            AVStream* pStream = null;

            for (var i = 0; i < _pFormatContext->nb_streams; i++)
            {
                if (_pFormatContext->streams[i]->codec->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    pStream = _pFormatContext->streams[i];
                    break;
                }
            }

            if (pStream == null)
            {
                throw new InvalidOperationException("Could not found video stream.");
            }

            _streamIndex = pStream->index;
            _pCodecContext = pStream->codec;

            var codecId = _pCodecContext->codec_id;
            var pCodec = ffmpeg.avcodec_find_decoder(codecId);

            if (pCodec == null)
            {
                throw new InvalidOperationException("Unsupported codec.");
            }

            ffmpeg.avcodec_open2(_pCodecContext, pCodec, null).ThrowExceptionIfError();

            var frameSize = new Size(_pCodecContext->width, _pCodecContext->height);
            var pixelFormat = _pCodecContext->pix_fmt;

            _videoFrameConverter = new VideoFrameConverter(frameSize, pixelFormat, frameSize, AVPixelFormat.AV_PIX_FMT_BGR24);

            _pPacket = ffmpeg.av_packet_alloc();
            _pFrame = ffmpeg.av_frame_alloc();
        }

        public Bitmap TakeSnapshot()
        {
            if (!TryDecodeNextFrame(out var frame))
            {
                return null;
            }

            var convertedFrame = _videoFrameConverter.Convert(frame);
            var bitmap = new Bitmap(convertedFrame.width, convertedFrame.height, convertedFrame.linesize[0], PixelFormat.Format24bppRgb, (IntPtr)convertedFrame.data[0]);

            return bitmap.Clone() as Bitmap;
        }

        public void Stop()
        {
            _videoFrameConverter.Dispose();

            ffmpeg.av_frame_unref(_pFrame);
            ffmpeg.av_free(_pFrame);

            ffmpeg.av_packet_unref(_pPacket);
            ffmpeg.av_free(_pPacket);

            ffmpeg.avcodec_close(_pCodecContext);
            var pFormatContext = _pFormatContext;
            ffmpeg.avformat_close_input(&pFormatContext);
        }

        public void Dispose()
        {
            Stop();
        }

        private bool TryDecodeNextFrame(out AVFrame frame)
        {
            ffmpeg.av_frame_unref(_pFrame);
            int error;

            do
            {
                try
                {
                    do
                    {
                        error = ffmpeg.av_read_frame(_pFormatContext, _pPacket);
                        if (error == ffmpeg.AVERROR_EOF)
                        {
                            frame = *_pFrame;
                            return false;
                        }

                        error.ThrowExceptionIfError();
                    } while (_pPacket->stream_index != _streamIndex);

                    ffmpeg.avcodec_send_packet(_pCodecContext, _pPacket).ThrowExceptionIfError();
                }
                finally
                {
                    ffmpeg.av_packet_unref(_pPacket);
                }

                error = ffmpeg.avcodec_receive_frame(_pCodecContext, _pFrame);
            } while (error == ffmpeg.AVERROR(ffmpeg.EAGAIN));

            error.ThrowExceptionIfError();
            frame = *_pFrame;

            return true;
        }

        private int InterruptCallback(void* args)
        {
            if (_connected) return 0;
            if ((DateTime.Now - _initDate) > Timeout) return 1;
            return 0;
        }
    }
}
