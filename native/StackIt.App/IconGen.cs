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
    /// 图标 = 一颗斜置胶囊：米白帽 + 琥珀身，靛蓝底。
    ///
    /// 版本史：一版「一条横杠」信息量为零；二版「四根柱子」被否——
    /// 形状太通用（像任何统计软件）、配色跟界面、不贴合「服用记录」主题。
    /// 三版候选画了五个母题（格 / 印 / 叠 / 板 / 囊），用户选了胶囊：
    /// 最直白、一眼药事、轮廓在任务栏里最干净。
    ///
    /// 配色**锁死字面值**，不再取运行时 Theme.Accent：ICO 是静态文件，
    /// 「跟着主题走」从来只在生成那一刻成立；而且应用是 2 纸底 × 6 强调色，
    /// 图标跟不了任何一套，不如定一套。靛蓝取自「靛」色相家族压深一档
    /// （冷静专业），米白/琥珀就是界面的纸色与默认强调色——图标和界面同族。
    ///
    /// 小尺寸要**单独一套形状**，不能只是把大图缩小：
    /// 斜置胶囊在 16px 上抗锯齿糊成一团，≤32 走 upright——直角底、
    /// 胶囊竖直、帽身缝隙加宽，保证托盘里仍是清楚的「两段一粒」。
    /// </summary>
    private static DrawingVisual Draw(int size = 256)
    {
        var v = new DrawingVisual();
        using var dc = v.RenderOpen();

        const double S = 256;
        var bg = new SolidColorBrush(Color.FromRgb(0x3E, 0x51, 0x90));   // 靛蓝底
        var cap = new SolidColorBrush(Color.FromRgb(0xF6, 0xEF, 0xE1));  // 米白帽
        var body = new SolidColorBrush(Color.FromRgb(0xD7, 0x9A, 0x3E)); // 琥珀身

        var small = size <= 32;

        // 底板：小尺寸直角满幅（圆角在 16px 上只剩锯齿），大图按 Win 惯例留白圆角
        var inset = small ? 1.0 : 10.0;
        dc.DrawGeometry(bg, null, Round(inset, inset, S - 2 * inset, S - 2 * inset, small ? 0 : 56));

        dc.PushTransform(new RotateTransform(small ? 0 : -32, 128, 128));
        var w = small ? 96.0 : 84.0;
        var x = 128 - w / 2;
        var top = small ? 34.0 : 30.0;
        var bottom = S - top;
        var gap = small ? 8.0 : 6.0;
        var mid = 128.0;
        var stadium = Round(x, top, w, bottom - top, w / 2);

        // 上半（帽）米白、下半（身）琥珀：同一颗体育场形各裁一半画两遍，
        // 中间留 gap 的缝——两段式是胶囊能被认出来的关键。
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, S, mid - gap / 2)));
        dc.DrawGeometry(cap, null, stadium);
        dc.Pop();
        dc.PushClip(new RectangleGeometry(new Rect(0, mid + gap / 2, S, S)));
        dc.DrawGeometry(body, null, stadium);
        dc.Pop();
        dc.Pop();

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