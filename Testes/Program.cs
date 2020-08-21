using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FFmpeg.AutoGen.Simple;

namespace Testes
{
    class Program
    {
        static void Main(string[] args)
        {
            VideoStreamDecoder.Initialize();

            var a = new VideoStreamDecoder();
            a.Start(@"C:\Users\rarndt\Desktop\coronga.avi");
            a.GetStreamConverter("teste.mp4")
                .Convert();
        }
    }
}
