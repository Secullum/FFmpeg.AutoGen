using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Simple;
using System.Drawing;
using System.IO;

namespace Testes
{
    class Program
    {
        static readonly string[] formats = { "avi", "mp4", "wmv", "vob", "mpg", "mpeg", "mov" };

        static void Main(string[] args)
        {
            VideoStreamDecoder.Initialize();

            var size = new Size(1024, 768);

            //foreach (var item in formats)
            //{
            //    new VideoStreamConverter(size, "./teste/input." + item)
            //        .Convert("./teste/output_" + item + ".mp4");
            //}

            //var a = new VideoStreamConverter(size, @"C:\Users\rarndt\Desktop\coronga.avi");

            //var b = a.GetInputFileInfo();

            //a.Convert("./teste_.mp4");

            using (var fs = new FileStream(@"C:\Users\rarndt\Desktop\coronga.avi", FileMode.Open))
            {

                new VideoStreamConverter(size, fs, "avi")
                    .Convert("testeeeee.mp4");

            }
        }
    }
}
