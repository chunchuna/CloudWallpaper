using System.Runtime.InteropServices;
using System.Text;

namespace CloudWallpaper;

/// <summary>最小化的 OpenGL 绑定（无第三方依赖）。</summary>
internal static unsafe class GL
{
    public const uint TEXTURE_2D = 0x0DE1, FLOAT = 0x1406, RED = 0x1903, R32F = 0x822E, LINEAR = 0x2601;
    public const uint TEXTURE_MAG_FILTER = 0x2800, TEXTURE_MIN_FILTER = 0x2801, TEXTURE_WRAP_S = 0x2802, TEXTURE_WRAP_T = 0x2803;
    public const uint CLAMP_TO_EDGE = 0x812F, FRAGMENT_SHADER = 0x8B30, VERTEX_SHADER = 0x8B31;
    public const uint COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82, QUADS = 0x0007, UNPACK_ALIGNMENT = 0x0CF5;

    [DllImport("opengl32.dll")] public static extern IntPtr wglCreateContext(IntPtr hdc);
    [DllImport("opengl32.dll")] public static extern bool wglMakeCurrent(IntPtr hdc, IntPtr ctx);
    [DllImport("opengl32.dll")] public static extern bool wglDeleteContext(IntPtr ctx);
    [DllImport("opengl32.dll", CharSet = CharSet.Ansi)] static extern IntPtr wglGetProcAddress(string name);

    [DllImport("opengl32.dll", EntryPoint = "glViewport")] public static extern void Viewport(int x, int y, int w, int h);
    [DllImport("opengl32.dll", EntryPoint = "glGenTextures")] public static extern void GenTextures(int n, out uint tex);
    [DllImport("opengl32.dll", EntryPoint = "glBindTexture")] public static extern void BindTexture(uint target, uint tex);
    [DllImport("opengl32.dll", EntryPoint = "glTexParameteri")] public static extern void TexParameteri(uint target, uint name, int value);
    [DllImport("opengl32.dll", EntryPoint = "glPixelStorei")] public static extern void PixelStorei(uint name, int value);
    [DllImport("opengl32.dll", EntryPoint = "glTexImage2D")] public static extern void TexImage2D(uint target, int level, int internalFormat, int w, int h, int border, uint format, uint type, float[] data);
    public const uint SRC_ALPHA = 0x0302;
    public const uint FRAMEBUFFER = 0x8D40, COLOR_ATTACHMENT0 = 0x8CE0, FRAMEBUFFER_COMPLETE = 0x8CD5, COLOR_BUFFER_BIT = 0x4000;
    [DllImport("opengl32.dll", EntryPoint = "glClear")] public static extern void Clear(uint mask);
    [DllImport("opengl32.dll", EntryPoint = "glClearColor")] public static extern void ClearColor(float r, float g, float b, float a);

    // ---- 离屏渲染目标（OpenGL 3.0）----
    static delegate* unmanaged[Stdcall]<int, uint*, void> _genFramebuffers;
    static delegate* unmanaged[Stdcall]<uint, uint, void> _bindFramebuffer;
    static delegate* unmanaged[Stdcall]<uint, uint, uint, uint, int, void> _framebufferTexture2D;
    static delegate* unmanaged[Stdcall]<uint, uint> _checkFramebufferStatus;

    static void LoadFramebufferFunctions()
    {
        _genFramebuffers = (delegate* unmanaged[Stdcall]<int, uint*, void>)Load("glGenFramebuffers");
        _bindFramebuffer = (delegate* unmanaged[Stdcall]<uint, uint, void>)Load("glBindFramebuffer");
        _framebufferTexture2D = (delegate* unmanaged[Stdcall]<uint, uint, uint, uint, int, void>)Load("glFramebufferTexture2D");
        _checkFramebufferStatus = (delegate* unmanaged[Stdcall]<uint, uint>)Load("glCheckFramebufferStatus");
    }

    public static uint GenFramebuffer() { uint f; _genFramebuffers(1, &f); return f; }
    public static void BindFramebuffer(uint fbo) => _bindFramebuffer(FRAMEBUFFER, fbo);

    /// <summary>新建（或按新尺寸重建）一张可以渲染进去的 RGBA 纹理，挂到 fbo 上。</summary>
    public static void AttachColorTexture(uint fbo, ref uint tex, int w, int h)
    {
        if (tex == 0) GenTextures(1, out tex);
        BindTexture(TEXTURE_2D, tex);
        TexParameteri(TEXTURE_2D, TEXTURE_MIN_FILTER, (int)LINEAR);
        TexParameteri(TEXTURE_2D, TEXTURE_MAG_FILTER, (int)LINEAR);
        TexParameteri(TEXTURE_2D, TEXTURE_WRAP_S, (int)CLAMP_TO_EDGE);
        TexParameteri(TEXTURE_2D, TEXTURE_WRAP_T, (int)CLAMP_TO_EDGE);
        TexImage2D(TEXTURE_2D, 0, (int)RGBA8, w, h, 0, BGRA, UNSIGNED_BYTE, IntPtr.Zero);
        _bindFramebuffer(FRAMEBUFFER, fbo);
        _framebufferTexture2D(FRAMEBUFFER, COLOR_ATTACHMENT0, TEXTURE_2D, tex, 0);
        if (_checkFramebufferStatus(FRAMEBUFFER) != FRAMEBUFFER_COMPLETE) throw new Exception("显卡不支持离屏渲染（帧缓冲不完整）");
        _bindFramebuffer(FRAMEBUFFER, 0);
    }
    public const uint BLEND = 0x0BE2, ONE = 1, ONE_MINUS_SRC_ALPHA = 0x0303, RGBA8 = 0x8058, BGRA = 0x80E1, UNSIGNED_BYTE = 0x1401;
    [DllImport("opengl32.dll", EntryPoint = "glTexImage2D")] public static extern void TexImage2D(uint target, int level, int internalFormat, int w, int h, int border, uint format, uint type, IntPtr data);
    [DllImport("opengl32.dll", EntryPoint = "glTexSubImage2D")] public static extern void TexSubImage2D(uint target, int level, int x, int y, int w, int h, uint format, uint type, IntPtr data);
    [DllImport("opengl32.dll", EntryPoint = "glEnable")] public static extern void Enable(uint cap);
    [DllImport("opengl32.dll", EntryPoint = "glDisable")] public static extern void Disable(uint cap);
    [DllImport("opengl32.dll", EntryPoint = "glBlendFunc")] public static extern void BlendFunc(uint src, uint dst);
    [DllImport("opengl32.dll", EntryPoint = "glTexCoord2f")] public static extern void TexCoord2f(float u, float v);
    [DllImport("opengl32.dll", EntryPoint = "glDeleteTextures")] public static extern void DeleteTextures(int n, ref uint tex);
    [DllImport("opengl32.dll", EntryPoint = "glBegin")] public static extern void Begin(uint mode);
    [DllImport("opengl32.dll", EntryPoint = "glEnd")] public static extern void End();
    [DllImport("opengl32.dll", EntryPoint = "glVertex2f")] public static extern void Vertex2f(float x, float y);

    static delegate* unmanaged[Stdcall]<uint, uint> _createShader;
    static delegate* unmanaged[Stdcall]<uint, int, byte**, int*, void> _shaderSource;
    static delegate* unmanaged[Stdcall]<uint, void> _compileShader;
    static delegate* unmanaged[Stdcall]<uint, uint, int*, void> _getShaderiv;
    static delegate* unmanaged[Stdcall]<uint, int, int*, byte*, void> _getShaderInfoLog;
    static delegate* unmanaged[Stdcall]<uint> _createProgram;
    static delegate* unmanaged[Stdcall]<uint, uint, void> _attachShader;
    static delegate* unmanaged[Stdcall]<uint, void> _linkProgram;
    static delegate* unmanaged[Stdcall]<uint, uint, int*, void> _getProgramiv;
    static delegate* unmanaged[Stdcall]<uint, int, int*, byte*, void> _getProgramInfoLog;
    static delegate* unmanaged[Stdcall]<uint, void> _useProgram;
    static delegate* unmanaged[Stdcall]<uint, byte*, int> _getUniformLocation;
    static delegate* unmanaged[Stdcall]<int, float, void> _uniform1f;
    static delegate* unmanaged[Stdcall]<int, float, float, void> _uniform2f;
    static delegate* unmanaged[Stdcall]<int, int, void> _uniform1i;
    static delegate* unmanaged[Stdcall]<int, int, float*, void> _uniform4fv;
    static delegate* unmanaged[Stdcall]<int, int> _swapInterval;

    static void* Load(string name, bool required = true)
    {
        IntPtr p = wglGetProcAddress(name);
        if (p == IntPtr.Zero && required) throw new Exception("显卡驱动不支持 OpenGL 函数: " + name);
        return (void*)p;
    }

    /// <summary>必须在 GL 上下文已激活后调用。</summary>
    public static void LoadExtensions()
    {
        _createShader = (delegate* unmanaged[Stdcall]<uint, uint>)Load("glCreateShader");
        _shaderSource = (delegate* unmanaged[Stdcall]<uint, int, byte**, int*, void>)Load("glShaderSource");
        _compileShader = (delegate* unmanaged[Stdcall]<uint, void>)Load("glCompileShader");
        _getShaderiv = (delegate* unmanaged[Stdcall]<uint, uint, int*, void>)Load("glGetShaderiv");
        _getShaderInfoLog = (delegate* unmanaged[Stdcall]<uint, int, int*, byte*, void>)Load("glGetShaderInfoLog");
        _createProgram = (delegate* unmanaged[Stdcall]<uint>)Load("glCreateProgram");
        _attachShader = (delegate* unmanaged[Stdcall]<uint, uint, void>)Load("glAttachShader");
        _linkProgram = (delegate* unmanaged[Stdcall]<uint, void>)Load("glLinkProgram");
        _getProgramiv = (delegate* unmanaged[Stdcall]<uint, uint, int*, void>)Load("glGetProgramiv");
        _getProgramInfoLog = (delegate* unmanaged[Stdcall]<uint, int, int*, byte*, void>)Load("glGetProgramInfoLog");
        _useProgram = (delegate* unmanaged[Stdcall]<uint, void>)Load("glUseProgram");
        _getUniformLocation = (delegate* unmanaged[Stdcall]<uint, byte*, int>)Load("glGetUniformLocation");
        _uniform1f = (delegate* unmanaged[Stdcall]<int, float, void>)Load("glUniform1f");
        _uniform2f = (delegate* unmanaged[Stdcall]<int, float, float, void>)Load("glUniform2f");
        _uniform1i = (delegate* unmanaged[Stdcall]<int, int, void>)Load("glUniform1i");
        _uniform4fv = (delegate* unmanaged[Stdcall]<int, int, float*, void>)Load("glUniform4fv");
        LoadFramebufferFunctions();
        _swapInterval = (delegate* unmanaged[Stdcall]<int, int>)Load("wglSwapIntervalEXT", false);
    }

    public static void SwapInterval(int n) { if (_swapInterval != null) _swapInterval(n); }
    public static void UseProgram(uint p) => _useProgram(p);
    public static void Uniform1f(int loc, float v) => _uniform1f(loc, v);
    public static void Uniform2f(int loc, float a, float b) => _uniform2f(loc, a, b);
    public static void Uniform1i(int loc, int v) => _uniform1i(loc, v);
    public static void Uniform4fv(int loc, int count, float[] values) { fixed (float* p = values) _uniform4fv(loc, count, p); }

    public static int GetUniformLocation(uint program, string name)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = bytes) return _getUniformLocation(program, p);
    }

    static uint Compile(uint type, string src)
    {
        uint sh = _createShader(type);
        byte[] bytes = Encoding.ASCII.GetBytes(src);
        fixed (byte* p = bytes)
        {
            byte* pp = p;
            int len = bytes.Length;
            _shaderSource(sh, 1, &pp, &len);
        }
        _compileShader(sh);
        int ok;
        _getShaderiv(sh, COMPILE_STATUS, &ok);
        if (ok == 0)
        {
            byte* log = stackalloc byte[4096];
            int n;
            _getShaderInfoLog(sh, 4096, &n, log);
            throw new Exception("着色器编译失败:\n" + Encoding.ASCII.GetString(log, n));
        }
        return sh;
    }

    public static uint BuildProgram(string vs, string fs)
    {
        uint prog = _createProgram();
        _attachShader(prog, Compile(VERTEX_SHADER, vs));
        _attachShader(prog, Compile(FRAGMENT_SHADER, fs));
        _linkProgram(prog);
        int ok;
        _getProgramiv(prog, LINK_STATUS, &ok);
        if (ok == 0)
        {
            byte* log = stackalloc byte[4096];
            int n;
            _getProgramInfoLog(prog, 4096, &n, log);
            throw new Exception("着色器链接失败:\n" + Encoding.ASCII.GetString(log, n));
        }
        return prog;
    }
}
