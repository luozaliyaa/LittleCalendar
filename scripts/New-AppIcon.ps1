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
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class IconWriter
{
    private static bool IsNearWhite(Color color)
    {
        return color.A > 0 && color.R >= 246 && color.G >= 246 && color.B >= 246;
    }

    private static Bitmap TrimOuterCanvas(Bitmap source)
    {
        int minX = source.Width, minY = source.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
                if (!IsNearWhite(source.GetPixel(x, y)))
                {
                    minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                }
        if (maxX < minX || maxY < minY) return new Bitmap(source);

        int side = Math.Max(maxX - minX + 1, maxY - minY + 1);
        int left = Math.Max(0, Math.Min(source.Width - side, ((minX + maxX + 1) / 2) - (side / 2)));
        int top = Math.Max(0, Math.Min(source.Height - side, ((minY + maxY + 1) / 2) - (side / 2)));
        var trimmed = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(trimmed))
            graphics.DrawImage(source, new Rectangle(0, 0, side, side), new Rectangle(left, top, side, side), GraphicsUnit.Pixel);

        var visited = new bool[side * side];
        var queue = new Queue<int>();
        Action<int, int> enqueue = (x, y) => {
            int index = y * side + x;
            if (visited[index] || !IsNearWhite(trimmed.GetPixel(x, y))) return;
            visited[index] = true; queue.Enqueue(index);
        };
        for (int x = 0; x < side; x++) { enqueue(x, 0); enqueue(x, side - 1); }
        for (int y = 1; y < side - 1; y++) { enqueue(0, y); enqueue(side - 1, y); }
        while (queue.Count > 0)
        {
            int index = queue.Dequeue(), x = index % side, y = index / side;
            trimmed.SetPixel(x, y, Color.Transparent);
            if (x > 0) enqueue(x - 1, y); if (x + 1 < side) enqueue(x + 1, y);
            if (y > 0) enqueue(x, y - 1); if (y + 1 < side) enqueue(x, y + 1);
        }
        return trimmed;
    }

    private static byte[] CreateDibFrame(Bitmap bitmap)
    {
        int width = bitmap.Width, height = bitmap.Height;
        int maskStride = ((width + 31) / 32) * 4;
        using (var stream = new MemoryStream())
        using (var output = new BinaryWriter(stream))
        {
            output.Write(40); output.Write(width); output.Write(height * 2);
            output.Write((ushort)1); output.Write((ushort)32); output.Write(0);
            output.Write(width * height * 4); output.Write(0); output.Write(0); output.Write(0); output.Write(0);
            for (int y = height - 1; y >= 0; y--)
                for (int x = 0; x < width; x++)
                {
                    Color color = bitmap.GetPixel(x, y);
                    output.Write(color.B); output.Write(color.G); output.Write(color.R); output.Write(color.A);
                }
            output.Write(new byte[maskStride * height]);
            return stream.ToArray();
        }
    }

    public static void Create(string sourcePath, string outputPath)
    {
        int[] sizes = { 16, 32, 48, 64, 128, 256 };
        byte[][] images = new byte[sizes.Length][];
        using (var source = new Bitmap(sourcePath))
        using (var trimmed = TrimOuterCanvas(source))
        {
            for (int i = 0; i < sizes.Length; i++)
            {
                using (var resized = new Bitmap(sizes[i], sizes[i], PixelFormat.Format32bppArgb))
                using (var graphics = Graphics.FromImage(resized))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.SmoothingMode = SmoothingMode.HighQuality;
                    graphics.DrawImage(trimmed, new Rectangle(0, 0, sizes[i], sizes[i]));
                    images[i] = CreateDibFrame(resized);
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
