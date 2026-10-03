using System.Diagnostics;
using CoreLoader;
using StoneShard;
using StoneshardTrials.Ui;

namespace StoneshardTrials.Intro;

/// <summary>
/// The lore intro: a full-screen slideshow in the GUI layer. Each picture drifts
/// in a slow pan and zoom, slides pass through black, and the narration is
/// wiped in line by line in the game's own colour text under the picture.
/// </summary>
/// <remarks>
/// <para>
/// Everything moves on a <see cref="Stopwatch"/>, not on frames, so it runs at
/// the same pace at any frame rate and through a hitch.
/// </para>
/// <para>
/// The narration sits on an opaque black band. That is what lets it fade:
/// colour text has no alpha of its own, but black laid over text that sits on
/// black is exactly the text faded out, so the reveal is a black mask pulled
/// off the line.
/// </para>
/// <para>
/// Input: every click is the intro's while it is open (pick mode, as the card
/// window does it). Keys are read in <see cref="CaptureKeys"/>, which the mod
/// calls just before it clears the game's keys; when nobody calls it, Update
/// reads them itself.
/// </para>
/// </remarks>
internal sealed class IntroScreen
{
    // Seconds.
    private const double FadeIn = 0.9, FadeOut = 0.6, SkipFade = 0.35, Unveil = 0.7;
    private const double FirstLine = 0.8, LineReveal = 1.6, LineGap = 0.35, Drift = 18;
    private const double InputGrace = 0.3, HookStale = 0.25;

    // Design space (960 x 540): the band the narration sits on, the bottom third.
    private const double BandFade = 340, BandSolid = 410, NarrationTop = 432, NarrationWrap = 820;
    private const double MaskEdge = 28;

    private const int VkEnter = 13, VkEscape = 27, VkSpace = 32;

    private readonly Canvas _canvas = new();
    private readonly Logger _log;
    private readonly Stopwatch _clock = new();
    private readonly Pictures _pictures;

    private Phase _phase = Phase.Closed;
    private int _index;
    private int _target;            // where a fade out goes: a slide, or Slides.All.Length for the end
    private double _slideStart;     // clock seconds when the current slide began
    private double _phaseStart;     // clock seconds when the current fade began
    private double _fadeLength;
    private bool _revealAll;
    private double _revealedAt = -1;
    private double _hookSeen = double.NegativeInfinity;
    private bool _advanceKey, _skipKey;
    private string _lastError = "";

    private enum Phase { Closed, Showing, FadingOut, Unveiling }

    public IntroScreen(Logger log, string modDirectory)
    {
        _log = log;
        _pictures = new Pictures(log, modDirectory);
    }

    public bool IsOpen => _phase != Phase.Closed;

    /// <summary>Raised once the intro has ended, by BEGIN, SKIP or Esc, after the game is unveiled.</summary>
    public event Action? Finished;

    private double Now => _clock.Elapsed.TotalSeconds;

    public void Start()
    {
        if (IsOpen) return;
        _clock.Restart();
        _advanceKey = _skipKey = false;
        _hookSeen = double.NegativeInfinity;
        _lastError = "";
        Input.ArmPick();
        Show(0);
        // The grace period counts from the first picture, not from before its decoding.
        _graceStart = Now;
        _failures = 0;
    }

    // When input starts counting, and the steps that failed in a row (see Update).
    private double _graceStart;
    private int _failures;
    // A step failing every frame, or an intro left up this long, ends it: the
    // player must never be stuck behind a slide with the game's input cleared.
    private const int MaxFailures = 30;
    private const double MaxLength = 300;

    /// <summary>Stops at once, without <see cref="Finished"/>, and lets go of the pictures.</summary>
    public void Close()
    {
        if (!IsOpen) return;
        _phase = Phase.Closed;
        _clock.Stop();
        try
        {
            if (Input.IsPicking) Input.CancelPick();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Report(ex); }
        _pictures.Release();
    }

    /// <summary>Frees the colour-text maps and pictures; for shutdown.</summary>
    public void Clear()
    {
        Close();
        _pictures.Release();
        try { _canvas.Clear(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Report(ex); }
    }

    /// <summary>
    /// Reads Space, Enter and Esc. The mod calls it from its o_controller Begin
    /// Step hook right before io_clear, the last moment the keys are still there.
    /// </summary>
    public void CaptureKeys()
    {
        if (!IsOpen) return;
        _hookSeen = Now;
        ReadKeys();
    }

    /// <summary>Every frame: clicks, keys and the timeline.</summary>
    public void Update()
    {
        if (!IsOpen) return;
        try
        {
            Step();
            _failures = 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Report(ex);
            if (++_failures >= MaxFailures) Finish();
        }
        if (IsOpen && Now > MaxLength) Finish();
    }

    public void Draw()
    {
        if (!IsOpen) return;
        // Any failure is the intro's own: it must never fault the mod every frame.
        try { DrawFrame(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Report(ex); }
    }

    private void Step()
    {
        // Nobody read the keys before the game cleared them: read them here.
        if (Now - _hookSeen > HookStale) ReadKeys();
        bool advance = _advanceKey, skip = _skipKey;
        _advanceKey = _skipKey = false;

        // Clicks in their own try: if they fail, the keys still move the intro on.
        try
        {
            if (!Input.IsPicking) Input.ArmPick();
            if (Input.TryTakePick(out _))
            {
                Input.ArmPick();
                var (x, y) = _canvas.ToDesign(Builtins.device_mouse_x_to_gui(0).AsReal, Builtins.device_mouse_y_to_gui(0).AsReal);
                switch (_canvas.ButtonAt(x, y))
                {
                    case "skip": Sfx("snd_button_click"); skip = true; break;
                    case "begin": Sfx("snd_button_click"); advance = true; break;
                    // On the last slide only BEGIN ends it; a click elsewhere just
                    // finishes the narration.
                    default:
                        if (_index < Slides.All.Length - 1 || !Revealed) advance = true;
                        else _revealAll = true;
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Report(ex); }

        // The key or click that brought the intro up is not a request to leave it.
        if (Now - _graceStart < InputGrace) advance = skip = false;

        switch (_phase)
        {
            case Phase.Showing:
                if (skip) FadeTo(Slides.All.Length, SkipFade);
                else if (advance)
                {
                    if (!Revealed) _revealAll = true;
                    else FadeTo(_index + 1, FadeOut);
                }
                break;
            case Phase.FadingOut:
                // Esc during an ordinary fade turns it into the end.
                if (skip && _target < Slides.All.Length) _target = Slides.All.Length;
                if (Now - _phaseStart < _fadeLength) break;
                if (_target >= Slides.All.Length)
                {
                    _phase = Phase.Unveiling;
                    _phaseStart = Now;
                    _pictures.Release();
                }
                else Show(_target);
                break;
            case Phase.Unveiling:
                if (Now - _phaseStart >= Unveil) Finish();
                break;
        }
    }

    private void Show(int index)
    {
        _index = index;
        _revealAll = false;
        _revealedAt = -1;
        // Loaded while the screen is black, so the hitch of decoding a full-size
        // PNG is never seen; the slide's clock starts after it.
        _pictures.Load(Slides.All[index], index);
        _slideStart = Now;
        _phase = Phase.Showing;
    }

    private void FadeTo(int target, double length)
    {
        _target = target;
        _fadeLength = length;
        _phaseStart = Now;
        _phase = Phase.FadingOut;
    }

    private void Finish()
    {
        Close();
        Finished?.Invoke();
    }

    private void ReadKeys()
    {
        try
        {
            if (Builtins.keyboard_check_pressed(VkEscape).AsBool) _skipKey = true;
            if (Builtins.keyboard_check_pressed(VkSpace).AsBool || Builtins.keyboard_check_pressed(VkEnter).AsBool) _advanceKey = true;
        }
        // Called from a game hook: nothing may escape it.
        catch (Exception ex) when (ex is not OutOfMemoryException) { Report(ex); }
    }

    // ---- timeline ----

    private double SlideTime => Now - _slideStart;

    /// <summary>How far line <paramref name="i"/> is revealed, 0 to 1.</summary>
    private double LineProgress(int i)
    {
        if (_revealAll) return 1;
        double start = FirstLine + i * (LineReveal + LineGap);
        return Math.Clamp((SlideTime - start) / LineReveal, 0, 1);
    }

    private bool Revealed => _revealAll || LineProgress(Slides.All[_index].Lines.Length - 1) >= 1;

    /// <summary>Seconds since the narration finished, for what appears after it.</summary>
    private double SinceRevealed()
    {
        if (!Revealed) return -1;
        if (_revealedAt < 0) _revealedAt = Now;
        return Now - _revealedAt;
    }

    /// <summary>The black laid over everything: 1 at the start of a slide and at the end of a fade.</summary>
    private double Blackout() => _phase switch
    {
        Phase.Showing => 1 - Smooth(SlideTime / FadeIn),
        Phase.FadingOut => Smooth((Now - _phaseStart) / _fadeLength),
        Phase.Unveiling => 1 - Smooth((Now - _phaseStart) / Unveil),
        _ => 0,
    };

    private static double Smooth(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    // ---- drawing ----

    private void DrawFrame()
    {
        var c = _canvas;
        c.Begin();
        double gw = GameDraw.GuiWidth, gh = GameDraw.GuiHeight;

        // The game shows through only while it is being unveiled at the end.
        if (_phase != Phase.Unveiling)
        {
            var slide = Slides.All[_index];
            FillGui(0, 0, gw, gh, 0, 1);
            if (!DrawPicture(slide, gw, gh)) DrawFallback(slide, gw, gh);
            DrawBand(gw, gh);
            DrawNarration(slide);
            DrawControls();
        }
        double black = Blackout();
        if (black > 0.001) FillGui(0, 0, gw, gh, 0, black);
    }

    /// <summary>The picture, covering the whole GUI, drifting and zooming over the slide; false when there is none.</summary>
    private bool DrawPicture(Slide slide, double gw, double gh)
    {
        if (_pictures.Get(_index) is not { } pic) return false;
        double p = Math.Clamp(SlideTime / Drift, 0, 1);
        // Ease out: the camera settles rather than stopping dead.
        p = 1 - (1 - p) * (1 - p);
        double zoom = slide.ZoomIn ? 1.04 + 0.10 * p : 1.14 - 0.10 * p;
        double scale = Math.Max(gw / pic.W, gh / pic.H) * zoom;
        double sw = pic.W * scale, sh = pic.H * scale;
        // The drift never uncovers an edge: at most the margin the zoom leaves.
        // The camera moves one way, so the picture slides the other.
        double travel = 2 * p - 1;
        double x = (gw - sw) / 2 - slide.PanX * travel * (sw - gw) / 2;
        double y = (gh - sh) / 2 - slide.PanY * travel * (sh - gh) / 2;

        // A slow pan needs filtering, or the picture steps a whole pixel at a time.
        bool filtered = Builtins.gpu_get_texfilter().AsBool;
        Builtins.gpu_set_texfilter(true);
        try { pic.Sprite.Draw(x, y, 0, scale, scale); }
        finally { Builtins.gpu_set_texfilter(filtered); }
        return true;
    }

    /// <summary>A dark gradient in the slide's colours with its title, when its picture is missing.</summary>
    private void DrawFallback(Slide slide, double gw, double gh)
    {
        int top = Bgr(slide.Top), bottom = Bgr(slide.Bottom);
        Builtins.draw_set_alpha(1);
        Builtins.draw_rectangle_colour(0, 0, gw, gh, top, top, bottom, bottom, false);
        _canvas.Text($"~w~{slide.Title}~/~", Canvas.DesignW / 2, 200, align: 0);
    }

    /// <summary>The black band the narration sits on, fading up into the picture.</summary>
    private void DrawBand(double gw, double gh)
    {
        var c = _canvas;
        double fadeTop = c.OriginY + BandFade * c.K, solidTop = c.OriginY + BandSolid * c.K;
        const int Strips = 16;
        double step = (solidTop - fadeTop) / Strips;
        for (int i = 0; i < Strips; i++)
        {
            double a = Math.Pow((i + 1) / (double)Strips, 1.6);
            FillGui(0, fadeTop + i * step, gw, step + 1, 0, a);
        }
        FillGui(0, solidTop, gw, gh - solidTop, 0, 1);
    }

    private void DrawNarration(Slide slide)
    {
        var c = _canvas;
        double y = NarrationTop;
        for (int i = 0; i < slide.Lines.Length; i++)
        {
            double r = LineProgress(i);
            if (r <= 0) break;
            var (w, h) = c.Text(slide.Lines[i], Canvas.DesignW / 2, y, align: 0, wrap: NarrationWrap);
            if (r < 1) Mask(Canvas.DesignW / 2 - w / 2, y - 2, w, h + 4, r);
            y += h + 8;
        }

        double since = SinceRevealed();
        if (since < 0) return;
        double a = Smooth(since / 0.6);
        if (_index < Slides.All.Length - 1)
        {
            // A slow pulse, so the prompt is noticed without nagging.
            double pulse = 0.55 + 0.45 * Math.Cos(since * 2.4);
            var (w, h) = c.Text("~gr~click or press Space to continue~/~", Canvas.DesignW - 24, 512, align: 1);
            c.Fill(new Area(Canvas.DesignW - 24 - w - 2, 510, w + 4, h + 4), 0, 1 - a * pulse);
        }
    }

    /// <summary>Black over the part of a line not yet revealed, with a soft leading edge.</summary>
    private void Mask(double x, double y, double w, double h, double r)
    {
        var c = _canvas;
        double edge = x - MaskEdge + (w + MaskEdge) * r;
        const int Steps = 7;
        double step = MaskEdge / Steps;
        for (int i = 0; i < Steps; i++)
            c.Fill(new Area(edge + i * step, y, step + 0.5, h), 0, (i + 1) / (double)(Steps + 1));
        double solid = edge + MaskEdge;
        if (solid < x + w + 2) c.Fill(new Area(solid, y, x + w + 2 - solid, h), 0, 1);
    }

    private void DrawControls()
    {
        var c = _canvas;
        bool last = _index == Slides.All.Length - 1;
        if (!last) c.Button("skip", "SKIP", new Area(Canvas.DesignW - 96, 14, 80, 22));
        if (!last || SinceRevealed() is not (var since and >= 0)) return;
        var begin = new Area(Canvas.DesignW / 2 - 70, 494, 140, 26);
        c.Button("begin", "BEGIN", begin);
        // Faded in over black, the same way the narration is.
        double a = Smooth(since / 0.8);
        if (a < 1) c.Fill(new Area(begin.X - 2, begin.Y - 2, begin.W + 4, begin.H + 4), 0, 1 - a);
    }

    private static void FillGui(double x, double y, double w, double h, int colour, double alpha)
    {
        // The draw state is shared with the game's own GUI: always put it back.
        try
        {
            Builtins.draw_set_alpha(alpha);
            Builtins.draw_set_colour(colour);
            Builtins.draw_rectangle(x, y, x + w, y + h, false);
        }
        finally
        {
            Builtins.draw_set_alpha(1);
            Builtins.draw_set_colour(0xFFFFFF);
        }
    }

    /// <summary>0xRRGGBB to GameMaker's 0xBBGGRR.</summary>
    private static int Bgr(int rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    private void Sfx(string name)
    {
        try
        {
            double id = _canvas.Asset(name);
            if (id >= 0) Builtins.audio_play_sound(id, 5, false);
        }
        catch (GmlException) { }
    }

    private void Report(Exception ex)
    {
        if (ex.Message != _lastError) _log.Warning($"intro: {ex.Message}");
        _lastError = ex.Message;
    }

    /// <summary>
    /// The slides' pictures, loaded one at a time as each slide comes up and
    /// let go of when the intro ends: six full-size pictures are 50 MB of
    /// texture that nothing needs once the game is under way.
    /// </summary>
    private sealed class Pictures
    {
        private readonly Logger _log;
        private readonly string _modDirectory;
        private readonly Dictionary<int, Picture> _loaded = new();
        private readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);

        public Pictures(Logger log, string modDirectory)
        {
            _log = log;
            _modDirectory = modDirectory;
        }

        public Picture? Get(int index) => _loaded.TryGetValue(index, out var p) ? p : null;

        /// <summary>Loads slide <paramref name="index"/>'s picture, dropping the one before; a missing or bad file leaves it to the fallback.</summary>
        public void Load(Slide slide, int index)
        {
            foreach (int other in _loaded.Keys.Where(k => k != index).ToList()) Drop(other);
            if (_loaded.ContainsKey(index)) return;
            string? path = Find(slide.File);
            if (path == null)
            {
                Warn(slide.File, "not found; drawing the placeholder gradient");
                return;
            }
            try
            {
                var sprite = Content.AddSprite(path);
                double w = sprite.Width, h = sprite.Height;
                if (w <= 0 || h <= 0)
                {
                    sprite.Dispose();
                    Warn(slide.File, "loaded empty; drawing the placeholder gradient");
                    return;
                }
                _loaded[index] = new Picture(sprite, w, h);
            }
            // A broken picture is a content problem, never a reason to stop the intro.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Warn(slide.File, ex.Message);
            }
        }

        public void Release()
        {
            foreach (int index in _loaded.Keys.ToList()) Drop(index);
        }

        private void Drop(int index)
        {
            if (!_loaded.Remove(index, out var p)) return;
            try { p.Sprite.Dispose(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _log.Warning($"intro: {ex.Message}"); }
        }

        // The mod may be handed either the shared Mods folder (where its dll
        // sits) or its own content folder; the pictures ship in the latter.
        private string? Find(string file)
        {
            foreach (var dir in new[]
            {
                Path.Combine(_modDirectory, "StoneshardTrials", "Intro", "slides"),
                Path.Combine(_modDirectory, "Intro", "slides"),
            })
            {
                string path = Path.Combine(dir, file);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        private void Warn(string file, string why)
        {
            if (_warned.Add(file)) _log.Warning($"intro: {file}: {why}");
        }
    }

    private sealed record Picture(Sprite Sprite, double W, double H);
}
