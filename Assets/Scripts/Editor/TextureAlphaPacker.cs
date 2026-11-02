using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Writes a separate alpha map into an albedo texture's alpha channel and saves the result as a new PNG.
///
/// Why: URP Lit (and most built-in shaders) have no alpha map slot; they read transparency from the Base
/// Map's alpha channel. Models exported from Blender often ship the alpha as its own greyscale texture.
///
/// The source files are read straight from disk (not through their import settings), so they don't need
/// Read/Write enabled and their compression doesn't degrade the result. The originals are never changed.
/// If the alpha map's size differs from the albedo's, it is resampled to the albedo's size (box-filtered
/// when shrinking, so thin strands average out instead of being skipped).
/// </summary>
public class TextureAlphaPacker : EditorWindow
{
    private enum Channel { Red, Green, Blue, Alpha, Luminance }

    [SerializeField] private Texture2D _albedo;
    [SerializeField] private Texture2D _alphaMap;
    [SerializeField] private Channel _channel = Channel.Red;
    [SerializeField] private bool _invert;
    [SerializeField] private string _outputName;
    [SerializeField] private bool _preserveCoverage = true;
    [SerializeField] private float _alphaCutoff = 0.5f;

    [MenuItem("Tools/Textures/Pack Alpha Into Albedo")]
    public static void Open() => GetWindow<TextureAlphaPacker>("Pack Alpha");

    private void OnGUI()
    {
        EditorGUILayout.HelpBox("Saves a copy of the albedo with the alpha map in its alpha channel, next to the " +
                                "albedo. Assign the result as the material's Base Map.", MessageType.Info);

        EditorGUI.BeginChangeCheck();
        _albedo = (Texture2D)EditorGUILayout.ObjectField("Albedo", _albedo, typeof(Texture2D), false);
        if (EditorGUI.EndChangeCheck() && _albedo != null) _outputName = _albedo.name + "Alpha";

        _alphaMap = (Texture2D)EditorGUILayout.ObjectField("Alpha Map", _alphaMap, typeof(Texture2D), false);
        _channel = (Channel)EditorGUILayout.EnumPopup(new GUIContent("Read Channel",
            "Greyscale maps: Red (or Luminance). Use Alpha if the map stores it in its own alpha channel."), _channel);
        _invert = EditorGUILayout.Toggle(new GUIContent("Invert", "For maps where black = opaque."), _invert);
        _outputName = EditorGUILayout.TextField("Output Name", _outputName);

        _preserveCoverage = EditorGUILayout.Toggle(new GUIContent("Preserve Coverage",
            "For Alpha Clipping materials (hair, lashes, lace): keeps cut-out areas from thinning and " +
            "vanishing in the smaller mipmaps at a distance."), _preserveCoverage);
        using (new EditorGUI.DisabledScope(!_preserveCoverage))
        {
            _alphaCutoff = EditorGUILayout.Slider(new GUIContent("Alpha Cutoff",
                "Match the material's Alpha Clipping threshold."), _alphaCutoff, 0f, 1f);
        }

        bool valid = _albedo != null && _alphaMap != null && !string.IsNullOrWhiteSpace(_outputName);
        using (new EditorGUI.DisabledScope(!valid))
        {
            if (GUILayout.Button("Pack")) Pack();
        }
    }

    private void Pack()
    {
        string albedoPath = AssetDatabase.GetAssetPath(_albedo);
        string alphaPath = AssetDatabase.GetAssetPath(_alphaMap);
        string outputPath = $"{Path.GetDirectoryName(albedoPath).Replace('\\', '/')}/{_outputName.Trim()}.png";

        if (outputPath == albedoPath || outputPath == alphaPath)
        {
            EditorUtility.DisplayDialog("Pack Alpha", "The output name matches a source texture; pick another name.", "OK");
            return;
        }
        if (File.Exists(outputPath) &&
            !EditorUtility.DisplayDialog("Pack Alpha", $"{outputPath} exists. Overwrite it?", "Overwrite", "Cancel"))
            return;

        var albedo = LoadFromDisk(albedoPath);
        var alpha = LoadFromDisk(alphaPath);
        if (albedo == null || alpha == null) return;

        try
        {
            int width = albedo.width, height = albedo.height;
            Color32[] colors = albedo.GetPixels32();
            float[] alphaValues = ResampleChannel(alpha, width, height);

            for (int i = 0; i < colors.Length; i++)
            {
                float a = _invert ? 1f - alphaValues[i] : alphaValues[i];
                colors[i].a = (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f);
            }

            var packed = new Texture2D(width, height, TextureFormat.RGBA32, false);
            packed.SetPixels32(colors);
            packed.Apply();
            File.WriteAllBytes(outputPath, packed.EncodeToPNG()); // overwriting keeps the .meta, so the GUID and material links survive
            DestroyImmediate(packed);
        }
        finally
        {
            DestroyImmediate(albedo);
            DestroyImmediate(alpha);
        }

        AssetDatabase.ImportAsset(outputPath);
        ConfigureImporter(albedoPath, outputPath);

        var result = AssetDatabase.LoadAssetAtPath<Texture2D>(outputPath);
        EditorGUIUtility.PingObject(result);
        string sizeNote = _alphaMap.width != _albedo.width || _alphaMap.height != _albedo.height
            ? $"\nAlpha map resized from {_alphaMap.width}x{_alphaMap.height}." : "";
        Debug.Log($"[TextureAlphaPacker] Wrote {outputPath}.{sizeNote}", result);
        EditorUtility.DisplayDialog("Pack Alpha", $"Wrote {outputPath}.{sizeNote}", "OK");
    }

    /// <summary>Copies the albedo's import settings (size, sRGB, wrap, filtering), then sets up the alpha.</summary>
    private void ConfigureImporter(string albedoPath, string outputPath)
    {
        var source = (TextureImporter)AssetImporter.GetAtPath(albedoPath);
        var target = (TextureImporter)AssetImporter.GetAtPath(outputPath);

        var settings = new TextureImporterSettings();
        source.ReadTextureSettings(settings);
        target.SetTextureSettings(settings);
        target.maxTextureSize = source.maxTextureSize;
        target.textureCompression = source.textureCompression;

        target.alphaSource = TextureImporterAlphaSource.FromInput;
        // Fills the colour under transparent pixels from the visible edge, so filtering and mipmaps don't
        // bleed a dark/odd fringe around cut-out strands.
        target.alphaIsTransparency = true;
        target.mipMapsPreserveCoverage = _preserveCoverage;
        target.alphaTestReferenceValue = _alphaCutoff;
        target.SaveAndReimport();
    }

    private static Texture2D LoadFromDisk(string path)
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (texture.LoadImage(File.ReadAllBytes(path))) return texture;

        DestroyImmediate(texture);
        EditorUtility.DisplayDialog("Pack Alpha", $"Couldn't read {path} (PNG/JPG only).", "OK");
        return null;
    }

    /// <summary>The chosen channel of the alpha map, resampled to width x height (0..1, row-major from the bottom).</summary>
    private float[] ResampleChannel(Texture2D source, int width, int height)
    {
        Color32[] pixels = source.GetPixels32();
        int sw = source.width, sh = source.height;
        var result = new float[width * height];

        if (sw == width && sh == height)
        {
            for (int i = 0; i < result.Length; i++) result[i] = Read(pixels[i]);
            return result;
        }

        // Each target pixel averages the source pixels it covers (box filter). When upscaling a box covers
        // at most one source pixel, which is a plain nearest lookup; fine for an alpha map.
        for (int y = 0; y < height; y++)
        {
            int y0 = y * sh / height, y1 = Mathf.Max(y0 + 1, (y + 1) * sh / height);
            for (int x = 0; x < width; x++)
            {
                int x0 = x * sw / width, x1 = Mathf.Max(x0 + 1, (x + 1) * sw / width);
                float sum = 0f;
                for (int sy = y0; sy < y1; sy++)
                    for (int sx = x0; sx < x1; sx++)
                        sum += Read(pixels[sy * sw + sx]);
                result[y * width + x] = sum / ((y1 - y0) * (x1 - x0));
            }
        }
        return result;
    }

    private float Read(Color32 c)
    {
        switch (_channel)
        {
            case Channel.Red: return c.r / 255f;
            case Channel.Green: return c.g / 255f;
            case Channel.Blue: return c.b / 255f;
            case Channel.Alpha: return c.a / 255f;
            default: return (0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b) / 255f;
        }
    }
}
