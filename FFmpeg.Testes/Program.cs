using FFmpeg.AutoGen.Simple;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FFmpeg.Testes
{
    class Program
    {
        static void Main(string[] args)
        {
            VideoStreamDecoder.Initialize();

            var files = Directory.GetFiles(@"d:\inputs\");

            foreach (var file in files)
            {
                var extension = Path.GetExtension(file);

                using (var ms = new FileStream(file, FileMode.Open))
                {
                    using (var converter = new VideoStreamConverter(new Size(1024, 768), ms))
                    {
                        //var info = converter.GetInputFileInfo();

                        converter.Convert("a.mp4", true);
                    }
                }
            }
        }
    }
}
