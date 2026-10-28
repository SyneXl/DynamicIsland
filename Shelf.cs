using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DynamicIsland;

sealed class Shelf
{
    const int ThumbnailSize = 112, IconSize = 96;
    const int ThumbnailOnly = 0x8, IconOnly = 0x4;
    const int BitsPerPixel = 32, BytesPerPixel = 4;
    const double Dpi = 96;

    public sealed class Item(string path, string? text = null, bool owned = false)
    {
        public string Path { get; } = path;
        public string Name => Text is { } text ? string.IsNullOrWhiteSpace(text) ? "Заметка" : Preview(text)
            : System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } name ? name : Path;
        public string? Text { get; internal set; } = text;
        public bool Owned { get; } = owned;
        public ImageSource? Picture { get; internal set; }
        public bool IsPhoto { get; internal set; }
    }

    readonly Dispatcher _ui;
    readonly List<Item> _items = [];
    readonly string _storage;
    readonly Action<string[]> _persist;
    public const int MaxTextLength = 1_000_000, MaxItems = 200, MaxImagePixels = 16_000_000;

    public Shelf(Dispatcher ui, string? storage = null, IEnumerable<string>? initial = null, Action<string[]>? persist = null)
    {
        _ui = ui;
        _storage = System.IO.Path.GetFullPath(storage ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland", "Shelf"));
        _persist = persist ?? (paths => { if (!Settings.TrySetShelf(paths)) throw new IOException("Не удалось сохранить полку"); });
        AddNew((initial ?? Settings.Shelf).Where(Exists));
    }

    public IReadOnlyList<Item> Items => _items;

    public event Action? Changed;

    public event Action<Item>? PictureLoaded;

    public bool Add(IEnumerable<string> paths)
    {
        string[] fresh = paths.Where(Exists).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !_items.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (fresh.Length == 0) return false;
        if (_items.Count + fresh.Length > MaxItems) throw new InvalidOperationException("На полке не больше 200 элементов");
        int before = _items.Count;
        if (!AddNew(fresh)) return false;
        try { Save(); }
        catch { _items.RemoveRange(before, _items.Count - before); throw; }
        return true;
    }

    public void Remove(Item item)
    {
        int index = _items.IndexOf(item);
        if (index < 0) return;
        _items.RemoveAt(index);
        try { Save(); }
        catch { _items.Insert(index, item); throw; }
        DeleteOwned(item);
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        Item[] removed = _items.ToArray();
        _items.Clear();
        try { Save(); }
        catch { _items.AddRange(removed); throw; }
        foreach (Item item in removed) DeleteOwned(item);
    }

    public static string Preview(string text)
    {
        ReadOnlySpan<char> preview = text.AsSpan().Trim();
        int length = Math.Min(preview.Length, 64);
        if (length > 0 && length < preview.Length && char.IsHighSurrogate(preview[length - 1])) length--;
        return new string(preview[..length]).Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    }

    bool IsOwned(string path) => string.Equals(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)), _storage, StringComparison.OrdinalIgnoreCase)
        && Guid.TryParseExact(System.IO.Path.GetFileNameWithoutExtension(path), "N", out _)
        && System.IO.Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".png";

    void DeleteOwned(Item item)
    {
        if (!item.Owned || !IsOwned(item.Path)) return;
        try { File.Delete(item.Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.Log(ex); }
    }

    public Item? AddText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Length > MaxTextLength) throw new InvalidOperationException("Текст слишком большой");
        if (_items.FirstOrDefault(i => i.Text == text) is { } duplicate) return duplicate;
        CheckRoom();
        string path = OwnedPath(".txt");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        var item = new Item(path, text, true);
        _items.Add(item);
        try { Save(); }
        catch { _items.Remove(item); DeleteOwned(item); throw; }
        return item;
    }

    public void UpdateText(Item item, string text)
    {
        if (!_items.Contains(item) || item.Text == null || !item.Owned || !IsOwned(item.Path)) throw new InvalidOperationException("Заметка больше не доступна");
        if (text.Length > MaxTextLength) throw new InvalidOperationException("Текст слишком большой");
        if (item.Text == text) return;
        string temporary = item.Path + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, item.Path, true);
        item.Text = text; Changed?.Invoke();
    }

    public bool AddImage(BitmapSource image)
    {
        if (image.PixelWidth <= 0 || image.PixelHeight <= 0 || (long)image.PixelWidth * image.PixelHeight > MaxImagePixels)
            throw new InvalidOperationException("Изображение слишком большое");
        CheckRoom();
        string path = OwnedPath(".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        try { using var stream = File.Create(path); encoder.Save(stream); }
        catch { File.Delete(path); throw; }
        try { return Add([path]); }
        catch { File.Delete(path); throw; }
    }

    // Explicit paste/drop only. Never watch the system clipboard or retain unrelated clipboard formats.
    public bool Paste(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files) return Add(files);
        if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource image) return AddImage(image);
        string? text = data.GetData(DataFormats.UnicodeText) as string ?? data.GetData(DataFormats.Text) as string;
        return text != null && AddText(text) != null;
    }

    public static bool CanPaste(IDataObject data) => data.GetDataPresent(DataFormats.FileDrop)
        || data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text);

    void CheckRoom() { if (_items.Count >= MaxItems) throw new InvalidOperationException("На полке уже 200 элементов"); }
    string OwnedPath(string extension)
    {
        Directory.CreateDirectory(_storage);
        return System.IO.Path.Combine(_storage, Guid.NewGuid().ToString("N") + extension);
    }

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    bool AddNew(IEnumerable<string> paths)
    {
        var added = new List<Item>();
        foreach (string path in paths)
        {
            if (_items.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            bool owned = IsOwned(path);
            string? text = null;
            if (owned && System.IO.Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            {
                try { if (new FileInfo(path).Length <= MaxTextLength * 4L) text = File.ReadAllText(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.Log(ex); }
            }
            var fresh = new Item(path, text, owned);
            _items.Add(fresh);
            added.Add(fresh);
        }
        if (added.Count == 0) return false;

        LoadPictures(added);
        return true;
    }

    void Save()
    {
        _persist(_items.Select(item => item.Path).ToArray());
        Changed?.Invoke();
    }

    void LoadPictures(List<Item> items)
    {
        var thread = new Thread(() =>
        {
            foreach (Item item in items)
            {
                if (item.Text != null) continue;
                if (item.Owned)
                {
                    try
                    {
                        using var stream = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                        var image = new BitmapImage();
                        image.BeginInit(); image.StreamSource = stream; image.CacheOption = BitmapCacheOption.OnLoad;
                        image.DecodePixelWidth = ThumbnailSize; image.EndInit(); image.Freeze();
                        _ui.InvokeAsync(() =>
                        {
                            if (!_items.Contains(item)) return;
                            item.Picture = image; item.IsPhoto = true; PictureLoaded?.Invoke(item);
                        });
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException) { App.Log(ex); }
                    continue;
                }
                BitmapSource? picture = ShellImage(item.Path, ThumbnailSize, ThumbnailOnly);
                bool photo = picture != null && IsOpaque(picture);
                if (!photo) picture = ShellImage(item.Path, IconSize, IconOnly);
                if (picture == null) continue;
                _ui.InvokeAsync(() =>
                {
                    item.Picture = picture;
                    item.IsPhoto = photo;
                    PictureLoaded?.Invoke(item);
                });
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    static bool IsOpaque(BitmapSource picture)
    {
        int w = picture.PixelWidth, h = picture.PixelHeight;
        var pixel = new byte[BytesPerPixel];
        foreach (var (x, y) in new[] { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1) })
        {
            picture.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, BytesPerPixel, 0);
            if (pixel[3] < 0xFF) return false;
        }
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr context, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr handle, int size, out BITMAP bitmap);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    static BitmapSource? ShellImage(string path, int size, int flags)
    {
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory) != 0) return null;
            int result = factory.GetImage(new SIZE { cx = size, cy = size }, flags, out bitmap);
            Marshal.ReleaseComObject(factory);
            if (result != 0 || bitmap == IntPtr.Zero) return null;

            if (GetObject(bitmap, Marshal.SizeOf<BITMAP>(), out BITMAP info) == 0 || info.bmBitsPixel != BitsPerPixel) return null;
            int width = info.bmWidth, height = Math.Abs(info.bmHeight);
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = BitsPerPixel,
            };
            var pixels = new byte[width * BytesPerPixel * height];
            IntPtr dc = GetDC(IntPtr.Zero);
            int rows = GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref header, 0);
            ReleaseDC(IntPtr.Zero, dc);
            if (rows != height) return null;
            var copy = BitmapSource.Create(width, height, Dpi, Dpi, PixelFormats.Pbgra32, null, pixels, width * BytesPerPixel);
            copy.Freeze();
            return copy;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
        }
    }
}
