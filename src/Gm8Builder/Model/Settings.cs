using Gm8Builder.IO;

namespace Gm8Builder.Model;

/// <summary>Global Game Settings, as the GM8.0 runner reads them (a zlib blob ahead of the encrypted stream).</summary>
public sealed class Settings
{
    public bool Fullscreen;
    public bool Interpolate;
    public bool NoBorder;
    public bool ShowCursor;
    /// <summary>Negative keeps the aspect ratio, 0 full scale, otherwise a percentage.</summary>
    public int Scaling;
    public bool AllowResize;
    public bool AlwaysOnTop;
    public uint ColourOutsideRoom;
    public bool SetResolution;
    public uint ColourDepth;
    public uint Resolution;
    public uint Frequency;
    public bool NoButtons;
    public bool VSync;
    public bool DisableScreensaver;
    public bool LetF4SwitchFullscreen;
    public bool LetF1ShowInfo;
    public bool LetEscEndGame;
    public bool LetF5SaveF6Load;
    public bool LetF9Screenshot;
    public bool TreatCloseAsEsc;
    public uint Priority;
    public bool FreezeOnLoseFocus;
    /// <summary>0 none, 1 default, 2 own images.</summary>
    public uint LoadingBar;
    public byte[]? LoadingBarBack;
    public byte[]? LoadingBarFront;
    public byte[]? LoadingImage;
    public bool LoadingTransparent;
    public uint LoadingAlpha;
    public bool ScaleProgressBar;
    public bool DisplayErrors;
    public bool WriteErrors;
    public bool AbortOnError;
    public bool TreatUninitialisedAsZero;
    /// <summary>Whatever follows the fields above. Empty in a GM8.0 build; kept so an unknown one still round-trips.</summary>
    public byte[] Trailer = [];

    public static Settings Read(ByteReader r)
    {
        var s = new Settings
        {
            Fullscreen = r.Bool(),
            Interpolate = r.Bool(),
            NoBorder = r.Bool(),
            ShowCursor = r.Bool(),
            Scaling = r.I32(),
            AllowResize = r.Bool(),
            AlwaysOnTop = r.Bool(),
            ColourOutsideRoom = r.U32(),
            SetResolution = r.Bool(),
            ColourDepth = r.U32(),
            Resolution = r.U32(),
            Frequency = r.U32(),
            NoButtons = r.Bool(),
            VSync = r.Bool(),
            DisableScreensaver = r.Bool(),
            LetF4SwitchFullscreen = r.Bool(),
            LetF1ShowInfo = r.Bool(),
            LetEscEndGame = r.Bool(),
            LetF5SaveF6Load = r.Bool(),
            LetF9Screenshot = r.Bool(),
            TreatCloseAsEsc = r.Bool(),
            Priority = r.U32(),
            FreezeOnLoseFocus = r.Bool(),
            LoadingBar = r.U32(),
        };
        if (s.LoadingBar != 0)
        {
            s.LoadingBarBack = MaybeChunk(r);
            s.LoadingBarFront = MaybeChunk(r);
        }
        s.LoadingImage = MaybeChunk(r);
        s.LoadingTransparent = r.Bool();
        s.LoadingAlpha = r.U32();
        s.ScaleProgressBar = r.Bool();
        s.DisplayErrors = r.Bool();
        s.WriteErrors = r.Bool();
        s.AbortOnError = r.Bool();
        s.TreatUninitialisedAsZero = r.Bool();
        s.Trailer = r.Bytes(r.Remaining);
        return s;
    }

    public void Write(ByteWriter w)
    {
        w.Bool(Fullscreen);
        w.Bool(Interpolate);
        w.Bool(NoBorder);
        w.Bool(ShowCursor);
        w.I32(Scaling);
        w.Bool(AllowResize);
        w.Bool(AlwaysOnTop);
        w.U32(ColourOutsideRoom);
        w.Bool(SetResolution);
        w.U32(ColourDepth);
        w.U32(Resolution);
        w.U32(Frequency);
        w.Bool(NoButtons);
        w.Bool(VSync);
        w.Bool(DisableScreensaver);
        w.Bool(LetF4SwitchFullscreen);
        w.Bool(LetF1ShowInfo);
        w.Bool(LetEscEndGame);
        w.Bool(LetF5SaveF6Load);
        w.Bool(LetF9Screenshot);
        w.Bool(TreatCloseAsEsc);
        w.U32(Priority);
        w.Bool(FreezeOnLoseFocus);
        w.U32(LoadingBar);
        if (LoadingBar != 0)
        {
            WriteMaybeChunk(w, LoadingBarBack);
            WriteMaybeChunk(w, LoadingBarFront);
        }
        WriteMaybeChunk(w, LoadingImage);
        w.Bool(LoadingTransparent);
        w.U32(LoadingAlpha);
        w.Bool(ScaleProgressBar);
        w.Bool(DisplayErrors);
        w.Bool(WriteErrors);
        w.Bool(AbortOnError);
        w.Bool(TreatUninitialisedAsZero);
        w.Bytes(Trailer);
    }

    /// <summary>[u32 exists]([u32 length][bytes])?</summary>
    private static byte[]? MaybeChunk(ByteReader r) => r.Bool() ? r.Chunk() : null;

    private static void WriteMaybeChunk(ByteWriter w, byte[]? data)
    {
        w.Bool(data != null);
        if (data != null) w.Chunk(data);
    }
}
