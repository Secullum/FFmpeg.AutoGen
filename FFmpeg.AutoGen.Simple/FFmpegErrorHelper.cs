using System;
using System.Runtime.InteropServices;

namespace FFmpeg.AutoGen.Simple
{
    internal static class FFmpegErrorHelper
    {
        public static int ThrowExceptionIfError(this int error, Action cleanupAction = null)
        {
            if (error < 0)
            {
                cleanupAction?.Invoke();

                throw new ApplicationException(av_strerror(error));
            }

            return error;
        }

        private static unsafe string av_strerror(int error)
        {
            var bufferSize = 1024;
            var buffer = stackalloc byte[bufferSize];
            ffmpeg.av_strerror(error, buffer, (ulong)bufferSize);
            var message = Marshal.PtrToStringAnsi((IntPtr)buffer);
            return message;
        }
    }
}
