param(
  [Parameter(Mandatory = $true)][string]$SourcePath,
  [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $SourcePath)) { throw "找不到图标源图片：$SourcePath" }

Add-Type -AssemblyName System.Drawing
$drawingAssembly = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\System.Drawing.dll'
Add-Type -ReferencedAssemblies $drawingAssembly -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class IconWriter
{
    public static void Create(string sourcePath, string outputPath)
    {
        int[] sizes = { 16, 32, 48, 64, 128, 256 };
        byte[][] images = new byte[sizes.Length][];
        using (var source = new Bitmap(sourcePath))
        {
            for (int i = 0; i < sizes.Length; i++)
            {
                using (var resized = new Bitmap(sizes[i], sizes[i], PixelFormat.Format32bppArgb))
                using (var graphics = Graphics.FromImage(resized))
                using (var stream = new MemoryStream())
                {
                    graphics.Clear(Color.Transparent);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.SmoothingMode = SmoothingMode.HighQuality;
                    graphics.DrawImage(source, new Rectangle(0, 0, sizes[i], sizes[i]));
                    resized.Save(stream, ImageFormat.Png);
                    images[i] = stream.ToArray();
                }
            }
        }

        string directory = Path.GetDirectoryName(outputPath);
        if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using (var output = new BinaryWriter(File.Create(outputPath)))
        {
            output.Write((ushort)0);
            output.Write((ushort)1);
            output.Write((ushort)sizes.Length);
            int offset = 6 + (16 * sizes.Length);
            for (int i = 0; i < sizes.Length; i++)
            {
                int size = sizes[i];
                output.Write((byte)(size == 256 ? 0 : size));
                output.Write((byte)(size == 256 ? 0 : size));
                output.Write((byte)0);
                output.Write((byte)0);
                output.Write((ushort)1);
                output.Write((ushort)32);
                output.Write(images[i].Length);
                output.Write(offset);
                offset += images[i].Length;
            }
            for (int i = 0; i < images.Length; i++) output.Write(images[i]);
        }
    }
}
'@

[IconWriter]::Create((Resolve-Path -LiteralPath $SourcePath), $OutputPath)
Write-Output "已生成图标：$OutputPath"
