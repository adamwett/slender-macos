using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Repairs the AssetRipper export of the Unity 3.5.4f1 build of Slender so it
/// loads correctly in Unity 2022.3. Two things cannot survive the 3.5 -> 2022.3
/// jump on their own:
///   1. Terrain was a MonoBehaviour backed by a built-in script (fileID 12000)
///      in 3.5; it became native class 218 in Unity 5. The component is dropped
///      on load and must be recreated with the settings captured from the
///      original scene YAML.
///   2. SplatPrototype was replaced by TerrainLayer assets in 2018.3.
/// Idempotent: safe to run repeatedly.
/// </summary>
public static class PortFixup
{
    const string ScenePath = "Assets/Slender.unity";
    const string TerrainDataPath = "Assets/TerrainData/New Terrain.asset";
    const string LayerDir = "Assets/TerrainData/Layers";

    // Captured verbatim from the original 3.5 scene YAML (MonoBehaviour &1354).
    const float TreeDistance = 2000f;
    const float TreeBillboardDistance = 80f;
    const float TreeCrossFadeLength = 5f;
    const int TreeMaximumFullLODCount = 160;
    const float DetailObjectDistance = 80f;
    const float DetailObjectDensity = 1f;
    const float HeightmapPixelError = 1f;
    const float SplatMapDistance = 40f;
    const int HeightmapMaximumLOD = 0;

    // SplatPrototype textures from the original TerrainData YAML, in layer order.
    static readonly string[] SplatGuids =
    {
        "3083601b157f3c14caec7dd7742a12e7",
        "e45b905fda632c544a50e8a78b2b1d17",
        "ff274f7e973eb1442a57d0e0f9063662",
    };
    static readonly Vector2 SplatTileSize = new Vector2(4f, 4f);

    [MenuItem("Port/Run Fixup")]
    public static void Run()
    {
        var log = new StringBuilder("=== PORT FIXUP ===\n");
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        int stripped = RemoveMissingScripts(log);
        var data = FixTerrainLayers(log);
        FixTerrain(data, log);
        RebindStubShaders(log);
        Audit(log);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        log.Append($"stripped {stripped} missing-script components\n=== FIXUP DONE ===");
        Debug.Log(log.ToString());

        // Batchmode turns an uncaught exception into a non-zero exit, so build.sh stops here.
        if (log.ToString().Contains("ERROR"))
            throw new System.Exception("PortFixup reported errors; see the log above");
    }

    /// <summary>
    /// AssetRipper's free edition cannot decompile D3D9 shader bytecode, so it emitted
    /// stubs for all 8 shaders the game shipped. Seven of them declared the SAME names as
    /// Unity's genuine built-in tree shaders and therefore shadowed them, which would have
    /// rendered the forest's leaves as opaque Lambert quads. The stub assets are deleted and
    /// their materials rebound by name to the real built-ins that 2022.3 still ships.
    /// "Skin/Diffuse" has no modern equivalent and falls back to Legacy Shaders/Diffuse.
    /// </summary>
    static readonly (string Material, string Shader)[] StubRebinds =
    {
        ("Assets/Material/Optimized Bark Material.mat",   "Hidden/Nature/Tree Creator Bark Optimized"),
        ("Assets/Material/Optimized Bark Material_0.mat", "Hidden/Nature/Tree Creator Bark Optimized"),
        ("Assets/Material/Optimized Bark Material_1.mat", "Hidden/Nature/Tree Creator Bark Optimized"),
        ("Assets/Material/Optimized Bark Material_2.mat", "Hidden/Nature/Tree Creator Bark Optimized"),
        ("Assets/Material/Optimized Bark Material_3.mat", "Hidden/Nature/Tree Creator Bark Optimized"),
        ("Assets/Material/Optimized Bark Material_4.mat", "Hidden/Nature/Tree Creator Bark Optimized"),
        ("Assets/Material/Optimized Leaf Material.mat",   "Hidden/Nature/Tree Creator Leaves Optimized"),
        ("Assets/Material/Optimized Leaf Material_0.mat", "Hidden/Nature/Tree Creator Leaves Optimized"),
        ("Assets/Material/Optimized Leaf Material_1.mat", "Hidden/Nature/Tree Creator Leaves Optimized"),
        ("Assets/Material/Optimized Leaf Material_2.mat", "Hidden/Nature/Tree Creator Leaves Optimized"),
        ("Assets/Material/Slenderman.mat",                "Legacy Shaders/Diffuse"),
        ("Assets/Material/tentacleblack.mat",             "Nature/Tree Creator Bark"),
    };

    static void RebindStubShaders(StringBuilder log)
    {
        foreach (var (path, shaderName) in StubRebinds)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { log.Append($"WARN: no material at {path}\n"); continue; }
            if (mat.shader != null && mat.shader.name == shaderName && mat.shader.isSupported)
                continue;

            var shader = Shader.Find(shaderName);
            if (shader == null) { log.Append($"ERROR: Shader.Find failed for '{shaderName}'\n"); continue; }
            mat.shader = shader;
            EditorUtility.SetDirty(mat);
            log.Append($"rebound {System.IO.Path.GetFileName(path)} -> {shaderName}\n");
        }
    }

    static int RemoveMissingScripts(StringBuilder log)
    {
        int n = 0;
        foreach (var go in AllSceneObjects())
            n += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
        return n;
    }

    static TerrainData FixTerrainLayers(StringBuilder log)
    {
        var data = AssetDatabase.LoadAssetAtPath<TerrainData>(TerrainDataPath);
        if (data == null) { log.Append($"ERROR: no TerrainData at {TerrainDataPath}\n"); return null; }

        log.Append($"TerrainData: heightmap {data.heightmapResolution}, size {data.size}, " +
                   $"alphamap {data.alphamapResolution}x{data.alphamapLayers}, " +
                   $"trees {data.treeInstanceCount}/{data.treePrototypes.Length} prototypes, " +
                   $"detail {data.detailPrototypes.Length} prototypes\n");

        if (data.terrainLayers != null && data.terrainLayers.Length > 0)
        {
            log.Append($"terrainLayers already present ({data.terrainLayers.Length}), leaving alone\n");
            return data;
        }

        if (!AssetDatabase.IsValidFolder(LayerDir))
            AssetDatabase.CreateFolder("Assets/TerrainData", "Layers");

        var layers = new List<TerrainLayer>();
        for (int i = 0; i < SplatGuids.Length; i++)
        {
            string texPath = AssetDatabase.GUIDToAssetPath(SplatGuids[i]);
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if (tex == null) { log.Append($"WARN: splat texture {SplatGuids[i]} not found\n"); continue; }

            string path = $"{LayerDir}/Splat{i}_{tex.name}.terrainlayer";
            var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
            if (layer == null)
            {
                layer = new TerrainLayer();
                AssetDatabase.CreateAsset(layer, path);
            }
            layer.diffuseTexture = tex;
            layer.tileSize = SplatTileSize;
            layer.tileOffset = Vector2.zero;
            EditorUtility.SetDirty(layer);
            layers.Add(layer);
            log.Append($"terrain layer {i}: {tex.name}\n");
        }
        data.terrainLayers = layers.ToArray();
        EditorUtility.SetDirty(data);
        return data;
    }

    static void FixTerrain(TerrainData data, StringBuilder log)
    {
        var go = AllSceneObjects().FirstOrDefault(g => g.name == "Terrain");
        if (go == null) { log.Append("ERROR: no GameObject named 'Terrain'\n"); return; }

        var terrain = go.GetComponent<Terrain>() ?? go.AddComponent<Terrain>();
        terrain.terrainData = data;
        terrain.treeDistance = TreeDistance;
        terrain.treeBillboardDistance = TreeBillboardDistance;
        terrain.treeCrossFadeLength = TreeCrossFadeLength;
        terrain.treeMaximumFullLODCount = TreeMaximumFullLODCount;
        terrain.detailObjectDistance = DetailObjectDistance;
        terrain.detailObjectDensity = DetailObjectDensity;
        terrain.heightmapPixelError = HeightmapPixelError;
        terrain.basemapDistance = SplatMapDistance;
        terrain.heightmapMaximumLOD = HeightmapMaximumLOD;
        terrain.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        terrain.drawHeightmap = true;
        terrain.drawTreesAndFoliage = true;

        // 3.5 terrain was Lambert-shaded; the Standard default would change the look.
        if (terrain.materialTemplate == null)
        {
            var mat = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Terrain-Diffuse.mat")
                   ?? AssetDatabase.GetBuiltinExtraResource<Material>("Default-Terrain-Standard.mat");
            terrain.materialTemplate = mat;
            log.Append($"terrain material: {(mat != null ? mat.name : "NONE")}\n");
        }

        var col = go.GetComponent<TerrainCollider>() ?? go.AddComponent<TerrainCollider>();
        col.terrainData = data;
        log.Append("terrain component restored\n");
    }

    static void Audit(StringBuilder log)
    {
        int nullMat = 0, nullShader = 0, missingScript = 0;
        var badShaders = new HashSet<string>();
        var shaderUse = new Dictionary<string, int>();
        foreach (var go in AllSceneObjects())
        {
            foreach (var mb in go.GetComponents<MonoBehaviour>())
                if (mb == null) missingScript++;

            var r = go.GetComponent<Renderer>();
            if (r == null) continue;
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) { nullMat++; continue; }
                // A dummy/stub shader still resolves to a named, non-null Shader object;
                // the only reliable signal that it will render pink is isSupported.
                if (m.shader == null || m.shader.name == "Hidden/InternalErrorShader"
                    || !m.shader.isSupported)
                {
                    nullShader++;
                    badShaders.Add($"{m.name} [{(m.shader == null ? "null" : m.shader.name)}] on '{go.name}'");
                }
                shaderUse.TryGetValue(m.shader == null ? "<null>" : m.shader.name, out int c);
                shaderUse[m.shader == null ? "<null>" : m.shader.name] = c + 1;
            }
        }
        bool clean = nullMat == 0 && nullShader == 0 && missingScript == 0;
        log.Append($"{(clean ? "" : "ERROR: ")}audit: {nullMat} null materials, {nullShader} broken shaders, " +
                   $"{missingScript} missing scripts\n");
        if (badShaders.Count > 0)
            log.Append("BROKEN:\n  " + string.Join("\n  ", badShaders) + "\n");
        log.Append("shaders in use:\n");
        foreach (var kv in shaderUse.OrderByDescending(k => k.Value))
            log.Append($"  {kv.Value,4}x {kv.Key}\n");

        foreach (var cam in Object.FindObjectsOfType<Camera>(true))
            log.Append($"camera '{cam.name}' enabled={cam.enabled} depth={cam.depth} " +
                       $"clear={cam.clearFlags} fov={cam.fieldOfView}\n");
    }

    static IEnumerable<GameObject> AllSceneObjects()
    {
        return Object.FindObjectsOfType<GameObject>(true);
    }
}
