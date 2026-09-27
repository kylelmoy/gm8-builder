namespace Gm8Builder.Tests;

/// <summary>A scratch directory, deleted afterwards.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("gm8-builder-test-").FullName;

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string Write(string relative, string text)
    {
        var p = Combine(relative.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, Latin1.Encode(text));
        return p;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public static class Fixtures
{
    /// <summary>tests/.../fixtures/tree, copied to the output directory.</summary>
    public static string Tree => System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", "tree");

    /// <summary>A private copy of the fixture tree, for tests that change it.</summary>
    public static string CopyTree(TempDir dir)
    {
        var target = dir.Combine("tree");
        foreach (var file in Directory.EnumerateFiles(Tree, "*", SearchOption.AllDirectories))
        {
            var dest = System.IO.Path.Combine(target, System.IO.Path.GetRelativePath(Tree, file));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
        return target;
    }

    /// <summary>
    /// A stand-in Game Maker 8 install: just an `fnames` file, in the engine's own
    /// format, with the handful of built-ins the lint tests call.
    /// </summary>
    public static string FakeGm8(TempDir dir)
    {
        dir.Write("gm8/fnames", string.Join("\r\n",
            "// fake fnames for tests",
            "point_distance(x1,y1,x2,y2)",
            "ds_grid_get(id,x,y)",
            "abs(x)",
            "show_message(str)",
            "instance_create(x,y,obj)",
            "execute_string(str,arg0,arg1,...)",
            "max(val1,val2,...)",
            "room_speed",
            "score*",
            "health*",
            "pi#",
            "c_white#",
            "view_xview[0..7]",
            ""));
        return dir.Combine("gm8");
    }
}
