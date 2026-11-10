namespace CloudWallpaper;

/// <summary>图标之外自由飘动的云：每朵云由几个云团组成，缓慢横向漂移，飘出屏幕后从另一侧重新进入。</summary>
internal sealed class DriftingClouds
{
    public const int MaxBlobs = 32;
    const int CloudCount = 6, BlobsPerCloud = 5;
    const float BlobEdge = 0.61f;   // 与 WallpaperForm 中的云团核函数一致

    sealed class Cloud
    {
        public float X, Y, Speed, Radius, Phase;
        public readonly float[] Shape = new float[BlobsPerCloud * 4];   // 相对偏移 x, y 与半径 x, y（以 Radius 为单位）
    }

    readonly Random _rng = new();
    readonly Cloud[] _clouds = new Cloud[CloudCount];
    int _w, _h;
    float _scale, _time;

    /// <summary>传给着色器的数据：每个云团 (中心 x, 中心 y, 影响半径 x, 影响半径 y)。</summary>
    public readonly float[] Blobs = new float[MaxBlobs * 4];
    public int Count => CloudCount * BlobsPerCloud;

    float Rand(float a, float b) => a + (float)_rng.NextDouble() * (b - a);

    void Respawn(Cloud c, bool anywhere)
    {
        c.Radius = Rand(70f, 135f) * _scale;
        c.Speed = Rand(7f, 20f) * _scale;
        c.Phase = Rand(0f, 6.28f);
        c.Y = Rand(0.06f, 0.94f) * _h;
        c.X = anywhere ? Rand(0f, _w) : -Margin(c);

        // 一个扁平的主体 + 四个大小不一的云泡
        c.Shape[0] = 0f; c.Shape[1] = 0f; c.Shape[2] = 1f; c.Shape[3] = 0.58f;
        for (int k = 1; k < BlobsPerCloud; k++)
        {
            float r = Rand(0.34f, 0.52f);
            c.Shape[k * 4] = -0.75f + 0.5f * (k - 1) + Rand(-0.12f, 0.12f);
            c.Shape[k * 4 + 1] = Rand(-0.34f, 0.22f);
            c.Shape[k * 4 + 2] = r;
            c.Shape[k * 4 + 3] = r;
        }
    }

    static float Margin(Cloud c) => c.Radius * 2.4f;

    public void Update(float dt, int w, int h, float scale)
    {
        if (w != _w || h != _h || scale != _scale)
        {
            _w = w; _h = h; _scale = scale;
            for (int i = 0; i < CloudCount; i++) Respawn(_clouds[i] ??= new Cloud(), true);
        }

        _time += dt;
        int n = 0;
        foreach (Cloud c in _clouds)
        {
            c.X += c.Speed * dt;
            if (c.X > _w + Margin(c)) Respawn(c, false);
            float bob = MathF.Sin(_time * 0.11f + c.Phase) * 10f * _scale;   // 轻微的上下浮动
            for (int k = 0; k < BlobsPerCloud; k++)
            {
                Blobs[n++] = c.X + c.Shape[k * 4] * c.Radius;
                Blobs[n++] = c.Y + bob + c.Shape[k * 4 + 1] * c.Radius;
                Blobs[n++] = c.Shape[k * 4 + 2] * c.Radius / BlobEdge;
                Blobs[n++] = c.Shape[k * 4 + 3] * c.Radius / BlobEdge;
            }
        }
    }
}
