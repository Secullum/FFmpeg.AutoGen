﻿using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace FFmpeg.AutoGen.Simple
{
    internal unsafe class VideoFrameConverter : IDisposable
    {
        private IntPtr _convertedFrameBufferPtr;
        private Size _destinationSize;
        private byte_ptrArray4 _dstData;
        private int_array4 _dstLinesize;
        private SwsContext* _pConvertContext;

        public VideoFrameConverter(Size sourceSize, AVPixelFormat sourcePixelFormat, Size destinationSize, AVPixelFormat destinationPixelFormat)
        {
            _destinationSize = destinationSize;

            _pConvertContext = ffmpeg.sws_getContext(sourceSize.Width, sourceSize.Height, sourcePixelFormat, destinationSize.Width, destinationSize.Height, destinationPixelFormat, ffmpeg.SWS_FAST_BILINEAR, null, null, null);

            if (_pConvertContext == null)
            {
                throw new ApplicationException("Could not initialize the conversion context.");
            }

            var convertedFrameBufferSize = ffmpeg.av_image_get_buffer_size(destinationPixelFormat, destinationSize.Width, destinationSize.Height, 1);
            _convertedFrameBufferPtr = Marshal.AllocHGlobal(convertedFrameBufferSize);
            _dstData = new byte_ptrArray4();
            _dstLinesize = new int_array4();

            ffmpeg.av_image_fill_arrays(ref _dstData, ref _dstLinesize, (byte*)_convertedFrameBufferPtr, destinationPixelFormat, destinationSize.Width, destinationSize.Height, 1);
        }

        public AVFrame Convert(AVFrame sourceFrame)
        {
            ffmpeg.sws_scale(_pConvertContext, sourceFrame.data, sourceFrame.linesize, 0, sourceFrame.height, _dstData, _dstLinesize);

            var data = new byte_ptrArray8();
            data.UpdateFrom(_dstData);
            var linesize = new int_array8();
            linesize.UpdateFrom(_dstLinesize);

            return new AVFrame
            {
                data = data,
                linesize = linesize,
                width = _destinationSize.Width,
                height = _destinationSize.Height
            };
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(_convertedFrameBufferPtr);
            ffmpeg.sws_freeContext(_pConvertContext);
        }
    }
}