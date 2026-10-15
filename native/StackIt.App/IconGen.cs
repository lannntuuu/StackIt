using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StackIt.App;

/// <summary>
/// 生成应用图标 app.ico，并写进 csproj（ApplicationIcon）。
///
/// 为什么不提交一张二进制图：ICO 是几个尺寸叠在一起的文件，
/// 看不出画了什么，改起来也只能靠猜。这里用代码画出来 ——
/// 图形和配色都留在源码里，想改就改，review 也看得懂。
///
/// 用法：StackIt.App.exe --make-icon [输出路径]
/// </summary>
internal static class IconGen
{
    /// <summary>
    /// 图标 = 一叠高低不一的柱子。
    ///
    /// 上一版是"一条横杠"，用户反馈很丑 —— 问题不在配色，在于形状：
    /// 一个圆角矩形里放一条圆角矩形，就是一个没有信息的胶囊，
    /// 缩到 16px 也没剩下任何可认的东西。
    ///
    /// 现在改成 4 根高低不同的柱子：既认得出"记录密度 / 频次"，
    /// 又是任何缩放尺寸下都成立的经典形状。
    ///
    /// 配色踩过一个坑：底色用 AccentDeep，渲染出来用户反馈"全黑"。
    /// AccentDeep 是色阶里的深棕档（琥珀色下就是 RGB 79,52,34），
    /// 它的设计用途是"深底承载文字"，当图标底色就成了看不出图案的黑饼。
    /// 改用 Accent —— 该色阶的正常饱和档，浅色柱子的对比也更好。
    ///
    /// 小尺寸要**单独一套形状**，不能只是把大图缩小：
    /// 圆角在 16px 上只剩锯齿，柱子之间那道缝直接糊掉。
    /// 所以 size 小于阈值时走 flat：直角、柱子更宽更少、留白更小。
    /// </summary>
    private static DrawingVisual Draw(int size = 256)
    {
        var v = new DrawingVisual();
        using var dc = v.RenderOpen();

        const double S = 256;
        // Accent 而不是 AccentDeep —— 后者是近黑的深棕，图标会变成黑饼。
        var bg = (Brush)Application.Current.Resources[Theme.Accent];
        var bar = Brushes.White;

        var small = size <= 32;

        // 小尺寸：直角、收紧留白、柱子更宽
        var inset = small ? 4.0 : 10.0;
        var radius = small ? 0.0 : 56.0;

        var padX = small ? 26.0 : 44.0;
        var padTop = small ? 32.0 : 42.0;
        var padBottom = small ? 32.0 : 44.0;
        var gap = small ? 8.0 : 14.0;

        // 小尺寸下 4 根柱子太挤，2 根会糊 —— 保持 4 根但把宽度给足
        double[] heights = small
            ? new[] { 0.45, 0.95, 0.65, 1.0 }
            : new[] { 0.40, 0.72, 0.55, 0.90 };

        dc.DrawGeometry(bg, null, Round(inset, inset, S - 2 * inset, S - 2 * inset, radius));

        var areaW = S - 2 * padX;
        var barW = (areaW - gap * (heights.Length - 1)) / heights.Length;
        var maxH = S - padTop - padBottom;

        for (var i = 0; i < heights.Length; i++)
        {
            var h = maxH * heights[i];
            var x = padX + i * (barW + gap);
            var y = S - padBottom - h;
            // 小尺寸柱子用直角，避免圆角吃掉本就很少的像素
            var r = small ? 0.0 : barW * 0.42;
            dc.DrawGeometry(bar, null, Round(x, y, barW, h, r));
        }

        return v;
    }

    /// <summary>WPF 没有 DrawRoundedRect，圆角矩形得自己拼几何。</summary>
    private static RectangleGeometry Round(double x, double y, double w, double h, double r)
    {
        r = Math.Min(r, Math.Min(w / 2, h / 2));
        return new RectangleGeometry(
            new Rect(x, y, w, h), r, r);
    }

    private static byte[] RenderPng(int size)
    {
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(Draw(256));
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// 把几个尺寸的 PNG 打进一个 ICO。
    ///
    /// 坑：Vista 起 ICO 允许直接存 PNG 整块，Windows / 资源管理器都认，
    /// 但 WPF 的 BitmapDecoder **认不了** —— XAML 里 Icon="app.ico" 会抛
    /// FileFormatException: 图像解码器无法对此图像进行解码。
    /// 所以这里老老实实写 32 位 BMP（DIB）条目：BGRA 自下而上，
    /// 带一行 AND 掩码（虽然 32 位时不透明，仍须写，占 4 字节每像素的对齐）。
    /// </summary>
    private static byte[] PackIco((int Size, byte[] Bgra)[] parts)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write((ushort)0);              // reserved
        w.Write((ushort)1);              // type = icon
        w.Write((ushort)parts.Length);

        // 先把每一项的 DIB 大小算出来，目录里的字节数和偏移才能一次写对。
        //
        // 坑：目录在文件前部，数据在后面，所以写目录时就得知道每项多大。
        // 如果一边写目录一边累加偏移，累加结果没机会写回去 ——
        // 目录里所有项的 offset 会都等于第一项的偏移（118），
        // Windows 取 256 那个（唯一真实存在的）凑合显示，
        // 而 WPF / 取小尺寸时取到的是越界数据，于是图标一片空白。
        var dibSizes = new int[parts.Length];
        var offsets = new int[parts.Length];
        var running = 6 + 16 * parts.Length;
        for (var i = 0; i < parts.Length; i++)
        {
            var (size, bgra) = parts[i];
            var xorSize = bgra.Length;
            var andRow = ((size + 31) / 32) * 4;   // 每行按 4 字节对齐
            dibSizes[i] = 40 + xorSize + andRow * size;
            offsets[i] = running;
            running += dibSizes[i];
        }

        for (var i = 0; i < parts.Length; i++)
        {
            var size = parts[i].Size;

            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);                        // 调色板数
            w.Write((byte)0);                        // reserved
            w.Write((ushort)1);                      // color planes
            w.Write((ushort)32);                     // bpp
            w.Write(dibSizes[i]);
            w.Write(offsets[i]);
        }

        for (var i = 0; i < parts.Length; i++)
        {
            var (size, bgra) = parts[i];
            var xorSize = bgra.Length;
            var andRow = ((size + 31) / 32) * 4;
            var andSize = andRow * size;

            // BITMAPINFOHEADER，高度写两倍 = 带 AND 掩码
            w.Write(40);
            w.Write(size);
            w.Write(size * 2);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(0);                  // BI_RGB
            w.Write(xorSize + andSize);
            w.Write(0);
            w.Write(0);
            w.Write(0);
            w.Write(0);

            w.Write(bgra);               // 自下而上的 BGRA

            for (var y = 0; y < size; y++)  // 全 0 = 完全不透明
                w.Write(new byte[andRow]);
        }

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// 渲染成 ICO DIB 要的 BGRA，自下而上。
    ///
    /// RenderTargetBitmap 只支持 Pbgra32（预乘），ICO DIB 要的是 Pbgra32
    /// 直接可用：图标不透明，没有半透明像素，预乘与不乘结果相同。
    /// </summary>
    private static byte[] RenderBgra(int size)
    {
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);

        // 关键：Draw() 里的坐标全是按 256 画布写的绝对单位。直接渲染成 16x16
        // 的位图时，只有左上角 16x16 单位会被画到 —— 那正好是圆角透明的角，
        // 于是小尺寸整帧全透明。必须先按比例缩放坐标系。
        var scale = size / 256.0;
        var v = Draw(size);
        v.Transform = new TransformGroup
        {
            Children = { new ScaleTransform(scale, scale) }
        };
        rtb.Render(v);

        var stride = size * 4;
        var px = new byte[stride * size];
        rtb.CopyPixels(px, stride, 0);

        // 诊断用：非零（不透明）像素个数。0 说明这一帧根本没画出来。
        var nonZero = 0;
        for (var i = 3; i < px.Length; i += 4)
            if (px[i] != 0) nonZero++;
        if (nonZero == 0)
            Console.WriteLine($"  警告：{size}x{size} 渲染出全透明，ICO 这一帧会是空白");

        // WPF 自上而下，ICO 的 DIB 要自下而上
        var outp = new byte[px.Length];
        for (var y = 0; y < size; y++)
            Buffer.BlockCopy(px, y * stride, outp, (size - 1 - y) * stride, stride);

        return outp;
    }

    public static bool Run(string[] args)
    {
        var outPath = args.Length > 1 ? args[1] : "app.ico";
        var pngDir = Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".";
        Directory.CreateDirectory(pngDir);

        // 16/24/32/48/64/128/256：任务栏、alt-tab、文件资源管理器、缩略图各取所需
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        var parts = sizes.Select(s => (Size: s, Bgra: RenderBgra(s))).ToArray();

        File.WriteAllBytes(outPath, PackIco(parts));

        // 顺带出一张 256 PNG，方便贴到 README / 图标预览
        var rtb = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(Draw(256));
        var f = new PngBitmapEncoder();
        f.Frames.Add(BitmapFrame.Create(rtb));
        using (var ms = new MemoryStream())
        {
            f.Save(ms);
            File.WriteAllBytes(Path.Combine(pngDir, "icon-256.png"), ms.ToArray());
        }

        Console.WriteLine($"已生成 {outPath}（{sizes.Length} 个尺寸，{new FileInfo(outPath).Length} 字节）");
        Environment.Exit(0);
        return true;
    }
}