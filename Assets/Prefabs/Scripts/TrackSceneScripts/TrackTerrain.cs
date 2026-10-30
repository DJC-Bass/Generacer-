using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

/// <summary>
/// Hills and mountains under the TrackScene, a new range every terrain round, shaped around that round's
/// track like a pin-impression toy: the ground grows as high as its mountains want EXCEPT under the track,
/// where it's pressed down to stay Track Clearance below the road, climbing away from it no steeper than
/// Valley Slope. So the course looks to run through the mountains in canyons, and never clips them.
///
/// Not every round: each track rolls Terrain Chance, the way the fog and blackout rounds roll theirs —
/// from the server's round seed in multiplayer, so everyone agrees. The rest are sky rounds, as before
/// there was any terrain. The three rolls are independent.
///
/// Shaped from the track the generator actually laid down — every road, loop and shoulder mesh — the
/// moment it finishes (TrackGenerator.Generated), before the car is placed. Built right there rather than
/// in the background: it takes about a second (the heights spread over every CPU core), and done in the
/// same frame that's part of the round's load — behind the ROADING screen in multiplayer — instead of a
/// hitch a second into the drive. It lives in the TrackScene and goes when that unloads. Seeded from the
/// round seed in multiplayer, so every player gets the same range around the (already identical) track.
///
/// Coloured with THIS ROUND's sky ground hue, so the land matches the sky's ground beyond it; steep
/// slopes get a darker shade of it. Solid, and by default touching it fails the run like the Kill Floor
/// — otherwise a car falling off would sink through a mountainside on its way down to it. Tuned on
/// Resources/TrackTerrainSettings.
/// </summary>
public class TrackTerrain : MonoBehaviour
{
    TrackTerrainSettings settings;
    static readonly System.Random rng = new System.Random();   // single-player seeds; leaves Unity's Random alone

    /// <summary>What one build made. The terrain's GameObject goes with its TrackScene; the assets behind
    /// it don't, so they're freed when that scene unloads.</summary>
    class Built
    {
        public Scene scene;
        public readonly List<Object> assets = new List<Object>();
    }
    readonly List<Built> built = new List<Built>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Bootstrap()
    {
        var settings = Resources.Load<TrackTerrainSettings>("TrackTerrainSettings");
        if (settings == null) return;   // no settings asset — no terrain
        var go = new GameObject("TrackTerrain");
        go.AddComponent<TrackTerrain>().settings = settings;
        DontDestroyOnLoad(go);
    }

    void OnEnable()
    {
        TrackGenerator.Generated += OnTrackGenerated;
        SceneManager.sceneUnloaded += OnSceneUnloaded;
    }

    void OnDisable()
    {
        TrackGenerator.Generated -= OnTrackGenerated;
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
    }

    void OnDestroy()
    {
        foreach (var b in built) Release(b);
        built.Clear();
    }

    void OnSceneUnloaded(Scene scene)
    {
        for (int i = built.Count - 1; i >= 0; i--)
            if (built[i].scene == scene)
            {
                Release(built[i]);
                built.RemoveAt(i);
            }
    }

    // Only the TrackScene's generator: the Tutorial and the endings lay out tracks of their own.
    void OnTrackGenerated(TrackGenerator generator)
    {
        Scene scene = generator.gameObject.scene;
        if (settings == null || !settings.enableTerrain || scene.name != TrackSceneName()) return;
        if (!RollTerrainRound()) return;

        var clock = Stopwatch.StartNew();
        var job = new TerrainJob(settings, RoundSeed());
        job.Gather(generator.transform);
        job.Run();
        if (!job.HasTrack) return;
        Create(scene, job);
        Debug.Log($"[TrackTerrain] {job.Size.x / 1000f:F0} x {job.Size.z / 1000f:F0} km, {job.Resolution}² heights " +
                  $"({job.Size.x / (job.Resolution - 1):F0} m apart) around {job.Triangles} track triangles, " +
                  $"built in {clock.ElapsedMilliseconds} ms ({job.WorkMs} ms of it the heights).");
    }

    /// <summary>Terrain round or sky round? Like the blackout roll, only during a game-loop run: with no
    /// GameLoopManager (the TrackScene opened on its own) the terrain is always built.</summary>
    bool RollTerrainRound()
    {
        var glm = GameLoopManager.Instance;
        if (glm == null) return true;

        bool terrainRound = (MultiplayerWorld.IsMultiplayerGame
            ? MultiplayerWorld.DeriveRandom("terrainround").NextDouble()
            : rng.NextDouble()) < settings.terrainChance;
        Debug.Log($"[TrackTerrain] Round {glm.RoundNumber}: {(terrainRound ? "terrain round" : "sky round")}.");
        return terrainRound;
    }

    static int RoundSeed() =>
        MultiplayerWorld.IsMultiplayerGame ? MultiplayerWorld.DeriveRandom("terrain").Next() : rng.Next();

    void Create(Scene scene, TerrainJob job)
    {
        var b = new Built { scene = scene };
        built.Add(b);

        var data = new TerrainData();
        b.assets.Add(data);
        data.heightmapResolution = job.Resolution;   // before the size: changing it rescales the size
        data.size = job.Size;
        data.SetHeights(0, 0, job.Heights);

        // One layer for the ground and one for the steep slopes, both in this round's sky-ground hue.
        float hue = GroundHue();
        data.alphamapResolution = job.AlphaResolution;
        data.terrainLayers = new[] { Layer(settings.groundColor, hue, b), Layer(settings.slopeColor, hue, b) };
        data.SetAlphamaps(0, 0, job.Alphamap);

        var go = Terrain.CreateTerrainGameObject(data);
        go.name = "TrackTerrain";
        SceneManager.MoveGameObjectToScene(go, scene);   // lives and dies with this round's TrackScene
        go.transform.position = job.Origin;

        var terrain = go.GetComponent<Terrain>();
        if (settings.terrainMaterial != null)
        {
            var material = new Material(settings.terrainMaterial);
            b.assets.Add(material);
            terrain.materialTemplate = material;
        }
        terrain.drawInstanced = true;
        terrain.heightmapPixelError = settings.pixelError;
        terrain.basemapDistance = 4000f;
        terrain.drawTreesAndFoliage = false;
        terrain.shadowCastingMode = ShadowCastingMode.On;

        ApplyContact(go, scene);
    }

    /// <summary>A terrain layer of one flat colour: <paramref name="color"/>'s saturation and brightness
    /// in <paramref name="hue"/>. Its texture has NO alpha channel: URP's terrain reads a layer texture's
    /// alpha as its smoothness map and locks the smoothness to 1 — an opaque colour came out mirror-glossy.</summary>
    static TerrainLayer Layer(Color color, float hue, Built b)
    {
        Color.RGBToHSV(color, out _, out float s, out float v);
        Color32 c = Color.HSVToRGB(hue, s, v);
        var texture = new Texture2D(4, 4, TextureFormat.RGB24, false) { name = "TrackTerrainColour", wrapMode = TextureWrapMode.Repeat };
        var pixels = new Color32[16];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = c;
        texture.SetPixels32(pixels);
        texture.Apply(false, true);

        var layer = new TerrainLayer { diffuseTexture = texture, tileSize = new Vector2(1000f, 1000f), smoothness = 0f, metallic = 0f };
        b.assets.Add(texture);
        b.assets.Add(layer);
        return layer;
    }

    /// <summary>This round's sky ground hue (0-1), read off this round's recoloured sky: the live one when
    /// it's this round's, or — in multiplayer, before anyone here is under it — a copy built from the round
    /// seed, which lands on the same colours. Falls back to Ground Color's own hue.</summary>
    float GroundHue()
    {
        Material owned = null;
        Material sky = SkyboxHueRandomizer.CurrentSky;
        if (sky == null && MultiplayerWorld.IsMultiplayerGame && settings.trackSkybox != null)
            sky = owned = SkyboxHueRandomizer.BuildRoundSky(settings.trackSkybox);

        float hue;
        if (sky != null && sky.HasProperty("_GroundColor")) Color.RGBToHSV(sky.GetColor("_GroundColor"), out hue, out _, out _);
        else Color.RGBToHSV(settings.groundColor, out hue, out _, out _);

        if (owned != null) Destroy(owned);
        return hue;
    }

    void ApplyContact(GameObject go, Scene scene)
    {
        switch (settings.groundContact)
        {
            case TrackTerrainSettings.GroundContact.None:
                Destroy(go.GetComponent<TerrainCollider>());
                break;

            case TrackTerrainSettings.GroundContact.EndsTheRun:
                // The scene's own Kill Floor knows which hub a failed run goes back to.
                KillFloor source = null;
                foreach (var root in scene.GetRootGameObjects())
                    if ((source = root.GetComponentInChildren<KillFloor>(true)) != null) break;
                if (source == null)
                {
                    Debug.LogWarning("[TrackTerrain] No Kill Floor in the TrackScene to copy — the terrain is just solid.");
                    break;
                }
                var floor = go.AddComponent<KillFloor>();
                floor.hubSceneIndex = source.hubSceneIndex;
                floor.hubSceneName = source.hubSceneName;
                floor.playerTag = source.playerTag;
                break;
        }
    }

    static void Release(Built b)
    {
        foreach (var asset in b.assets)
            if (asset != null) Destroy(asset);
    }

    static string TrackSceneName()
    {
        var glm = GameLoopManager.Instance;
        return glm != null ? glm.trackSceneName : "TrackScene";
    }
}

/// <summary>
/// One terrain's heights and colour weights. <see cref="Gather"/> copies out the track; <see cref="Run"/>
/// does the rest in plain arrays and maths — no Unity objects — so its big loops can be spread over every
/// CPU core.
///
/// The heights are noise — rolling hills everywhere, ridged mountain ranges over part of the land —
/// held under a CEILING the track sets: every sample under a piece of track may reach no higher than
/// that piece's lowest point less the clearance, and from there the ceiling climbs away at the valley
/// slope (a distance transform: two sweeps over the grid). The ground is the lower of the two, blended
/// softly where they meet. Samples are stamped across each track triangle's whole footprint plus the
/// grid squares around it, so the ground BETWEEN samples — the triangles the terrain is drawn with —
/// stays under the road too, not just the samples themselves.
/// </summary>
sealed class TerrainJob
{
    const float Open = 1e30f;   // no ceiling here

    // Settings, copied on the main thread.
    readonly float floor, hillHeight, hillFreq, mountainHeight, mountainFreq, regionFreq, warpFreq, warp, regionThreshold;
    readonly float clearance, slope, softness, margin, slopeTanStart, slopeTanEnd;
    readonly int requestedResolution;
    readonly float[] offsets = new float[12];

    // The track, gathered on the main thread.
    readonly List<(Vector3[] vertices, int[] triangles, Matrix4x4 toWorld)> meshes = new List<(Vector3[], int[], Matrix4x4)>();
    readonly List<Bounds> boxes = new List<Bounds>();

    // Results.
    public bool HasTrack;
    public int Resolution, AlphaResolution, Triangles;
    public long WorkMs;
    public Vector3 Origin, Size;
    public float[,] Heights;     // 0-1 across Size.y, [z, x] as TerrainData takes them
    public float[,,] Alphamap;   // [z, x, layer]: ground, slope

    public TerrainJob(TrackTerrainSettings s, int seed)
    {
        floor = s.floorAltitude;
        hillHeight = s.hillHeight;
        hillFreq = 1f / Mathf.Max(s.hillSize, 1f);
        mountainHeight = s.mountainHeight;
        mountainFreq = 1f / Mathf.Max(s.mountainSize, 1f);
        regionFreq = mountainFreq / 3f;
        warpFreq = mountainFreq / 1.5f;
        warp = 0.3f * s.mountainSize;
        regionThreshold = CoverageThreshold(s.mountainCoverage);
        clearance = s.trackClearance;
        slope = Mathf.Max(s.valleySlope, 0.01f);
        softness = s.valleySoftness;
        margin = s.margin;
        slopeTanStart = Mathf.Tan(Mathf.Clamp(s.slopeStart, 0f, 89f) * Mathf.Deg2Rad);
        slopeTanEnd = Mathf.Max(Mathf.Tan(Mathf.Clamp(s.slopeEnd, 0f, 89f) * Mathf.Deg2Rad), slopeTanStart + 1e-3f);
        requestedResolution = s.resolution;

        var r = new System.Random(seed);
        for (int i = 0; i < offsets.Length; i++) offsets[i] = (float)(r.NextDouble() * 1000.0);
    }

    /// <summary>Main thread: copies out every piece of the track — its roads, loops, shoulders and rails
    /// as triangles; anything whose mesh can't be read (an imported model, like the finish portal) as its
    /// bounding box. Anything under a Rigidbody (a car) moves, so it's left out.</summary>
    public void Gather(Transform track)
    {
        foreach (var filter in track.GetComponentsInChildren<MeshFilter>())
        {
            var mesh = filter.sharedMesh;
            if (mesh == null || filter.GetComponentInParent<Rigidbody>() != null) continue;
            if (mesh.isReadable) meshes.Add((mesh.vertices, mesh.triangles, filter.transform.localToWorldMatrix));
            else if (filter.TryGetComponent(out Renderer renderer)) boxes.Add(renderer.bounds);
        }
    }

    public void Run()
    {
        var clock = Stopwatch.StartNew();

        // ---- Where the track is ----
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var (vertices, triangles, toWorld) in meshes)
        {
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 w = vertices[i] = toWorld.MultiplyPoint3x4(vertices[i]);
                if (w.x < minX) minX = w.x;
                if (w.x > maxX) maxX = w.x;
                if (w.z < minZ) minZ = w.z;
                if (w.z > maxZ) maxZ = w.z;
            }
            Triangles += triangles.Length / 3;
        }
        foreach (var box in boxes)
        {
            minX = Mathf.Min(minX, box.min.x); maxX = Mathf.Max(maxX, box.max.x);
            minZ = Mathf.Min(minZ, box.min.z); maxZ = Mathf.Max(maxZ, box.max.z);
        }
        if (minX > maxX) return;   // nothing to build around
        HasTrack = true;

        // ---- The grid ----
        int n = Resolution = ValidResolution(requestedResolution);
        float x0 = minX - margin, z0 = minZ - margin;
        float sizeX = maxX - minX + 2f * margin, sizeZ = maxZ - minZ + 2f * margin;
        float dx = sizeX / (n - 1), dz = sizeZ / (n - 1);

        // ---- The ceiling the track presses into the ground ----
        var ceiling = new float[n * n];
        for (int i = 0; i < ceiling.Length; i++) ceiling[i] = Open;

        void Stamp(float xMin, float xMax, float zMin, float zMax, float limit)
        {
            // Every sample of every grid square the footprint touches.
            int j0 = Mathf.Clamp(Mathf.FloorToInt((xMin - x0) / dx), 0, n - 1), j1 = Mathf.Clamp(Mathf.CeilToInt((xMax - x0) / dx), 0, n - 1);
            int i0 = Mathf.Clamp(Mathf.FloorToInt((zMin - z0) / dz), 0, n - 1), i1 = Mathf.Clamp(Mathf.CeilToInt((zMax - z0) / dz), 0, n - 1);
            for (int i = i0; i <= i1; i++)
                for (int j = j0, k = i * n + j0; j <= j1; j++, k++)
                    if (limit < ceiling[k]) ceiling[k] = limit;
        }

        foreach (var (vertices, triangles, _) in meshes)
            for (int t = 0; t < triangles.Length; t += 3)
            {
                Vector3 a = vertices[triangles[t]], b = vertices[triangles[t + 1]], c = vertices[triangles[t + 2]];
                Stamp(Mathf.Min(a.x, Mathf.Min(b.x, c.x)), Mathf.Max(a.x, Mathf.Max(b.x, c.x)),
                      Mathf.Min(a.z, Mathf.Min(b.z, c.z)), Mathf.Max(a.z, Mathf.Max(b.z, c.z)),
                      Mathf.Min(a.y, Mathf.Min(b.y, c.y)) - clearance);
            }
        foreach (var box in boxes)
            Stamp(box.min.x, box.max.x, box.min.z, box.max.z, box.min.y - clearance);

        // From each stamped sample the ceiling climbs at the valley slope: forward and back sweeps, each
        // sample taking the lowest of its own ceiling and its already-swept neighbours' plus the climb.
        float upX = slope * dx, upZ = slope * dz, upD = slope * Mathf.Sqrt(dx * dx + dz * dz);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                int k = i * n + j;
                float v = ceiling[k];
                if (j > 0) v = Mathf.Min(v, ceiling[k - 1] + upX);
                if (i > 0)
                {
                    v = Mathf.Min(v, ceiling[k - n] + upZ);
                    if (j > 0) v = Mathf.Min(v, ceiling[k - n - 1] + upD);
                    if (j < n - 1) v = Mathf.Min(v, ceiling[k - n + 1] + upD);
                }
                ceiling[k] = v;
            }
        for (int i = n - 1; i >= 0; i--)
            for (int j = n - 1; j >= 0; j--)
            {
                int k = i * n + j;
                float v = ceiling[k];
                if (j < n - 1) v = Mathf.Min(v, ceiling[k + 1] + upX);
                if (i < n - 1)
                {
                    v = Mathf.Min(v, ceiling[k + n] + upZ);
                    if (j < n - 1) v = Mathf.Min(v, ceiling[k + n + 1] + upD);
                    if (j > 0) v = Mathf.Min(v, ceiling[k + n - 1] + upD);
                }
                ceiling[k] = v;
            }

        // ---- The ground: noise, flattened toward the edges, held under the ceiling ----
        var meters = new float[n * n];
        var rowMin = new float[n];
        var rowMax = new float[n];
        float fade = Mathf.Max(margin * 0.5f, 1f);
        Parallel.For(0, n, i =>
        {
            float z = z0 + i * dz, lo = float.MaxValue, hi = float.MinValue;
            for (int j = 0; j < n; j++)
            {
                float x = x0 + j * dx;
                float h = Elevation(x, z);

                // Down to the plains toward the edge, so the land ends on flat ground rather than a cliff.
                float edge = Mathf.Min(Mathf.Min(x - x0, x0 + sizeX - x), Mathf.Min(z - z0, z0 + sizeZ - z));
                h = floor + (h - floor) * SmoothStep01(edge / fade);

                float c = ceiling[i * n + j];
                h = Mathf.Min(SoftMin(h, c, softness), c);
                meters[i * n + j] = h;
                if (h < lo) lo = h;
                if (h > hi) hi = h;
            }
            rowMin[i] = lo;
            rowMax[i] = hi;
        });

        float bottom = float.MaxValue, top = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            bottom = Mathf.Min(bottom, rowMin[i]);
            top = Mathf.Max(top, rowMax[i]);
        }
        float range = Mathf.Max(top - bottom, 1f);
        Origin = new Vector3(x0, bottom, z0);
        Size = new Vector3(sizeX, range, sizeZ);

        Heights = new float[n, n];
        Parallel.For(0, n, i =>
        {
            for (int j = 0; j < n; j++) Heights[i, j] = (meters[i * n + j] - bottom) / range;
        });

        // ---- Colour: ground, turning to the slope colour as the ground steepens ----
        int na = AlphaResolution = Mathf.Clamp((n - 1) / 2, 16, 2048);   // n - 1 is a power of two
        Alphamap = new float[na, na, 2];
        Parallel.For(0, na, ai =>
        {
            int i = Mathf.Clamp(Mathf.RoundToInt((ai + 0.5f) / na * (n - 1)), 1, n - 2);
            for (int aj = 0; aj < na; aj++)
            {
                int j = Mathf.Clamp(Mathf.RoundToInt((aj + 0.5f) / na * (n - 1)), 1, n - 2);
                int k = i * n + j;
                float gx = (meters[k + 1] - meters[k - 1]) / (2f * dx);
                float gz = (meters[k + n] - meters[k - n]) / (2f * dz);
                float steep = SmoothStep01((Mathf.Sqrt(gx * gx + gz * gz) - slopeTanStart) / (slopeTanEnd - slopeTanStart));
                Alphamap[ai, aj, 0] = 1f - steep;
                Alphamap[ai, aj, 1] = steep;
            }
        });

        WorkMs = clock.ElapsedMilliseconds;
    }

    static int ValidResolution(int requested)
    {
        foreach (int r in new[] { 513, 1025, 2049 })
            if (requested <= r + (r - 1) / 2) return r;
        return 4097;
    }

    /// <summary>The ground before the track has its say: rolling hills everywhere, and ridged mountains
    /// over the part of the land the (very broad) region noise marks out for them. The domain is warped a
    /// little so ridges and valleys wander instead of lining up.
    ///
    /// Each layer is stretched over its MEASURED spread (200,000 samples), so the settings mean what they
    /// say: hills span 0 to Hill Height, the highest ridges reach Mountain Height, and Mountain Coverage is
    /// the share of the land that's mountain range. Raw, the noise uses only a fraction of its -1..1.</summary>
    float Elevation(float x, float z)
    {
        float wx = x + warp * Noise(x * warpFreq + offsets[0], z * warpFreq + offsets[1]);
        float wz = z + warp * Noise(x * warpFreq + offsets[2], z * warpFreq + offsets[3]);

        // 4-octave fbm: 5-95% of it lies within ±0.27.
        float hills = Mathf.Clamp01(Fbm(wx * hillFreq + offsets[4], wz * hillFreq + offsets[5], 4) / 0.55f + 0.5f) * hillHeight;

        float region = Fbm(wx * regionFreq + offsets[6], wz * regionFreq + offsets[7], 2);
        float mask = SmoothStep01((region - regionThreshold + 0.06f) / 0.12f);
        if (mask <= 0f) return floor + hills;

        // 6-octave ridged: 5-99% of it lies within 0.2-0.9. Squared, so the valleys between ridges stay low.
        float ridges = Mathf.Clamp01((Ridged(wx * mountainFreq + offsets[8], wz * mountainFreq + offsets[9], 6) - 0.2f) / 0.7f);
        return floor + hills + mask * ridges * ridges * mountainHeight;
    }

    /// <summary>The level of the region noise (2-octave fbm) that <paramref name="coverage"/> of the land
    /// lies above, from its measured spread.</summary>
    static float CoverageThreshold(float coverage)
    {
        float[] share = { 0f, 0.05f, 0.25f, 0.5f, 0.75f, 0.95f, 1f };
        float[] level = { 0.8f, 0.326f, 0.135f, 0f, -0.135f, -0.326f, -0.8f };
        coverage = Mathf.Clamp01(coverage);
        for (int i = 1; i < share.Length; i++)
            if (coverage <= share[i])
                return Mathf.Lerp(level[i - 1], level[i], (coverage - share[i - 1]) / (share[i] - share[i - 1]));
        return level[level.Length - 1];
    }

    static float Fbm(float x, float y, int octaves)
    {
        float sum = 0f, amp = 1f, norm = 0f;
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * Noise(x, y);
            norm += amp;
            x = x * 2.03f + 17.1f;
            y = y * 2.03f + 31.7f;
            amp *= 0.5f;
        }
        return sum / norm;
    }

    /// <summary>Ridged multifractal: sharp crests where the noise crosses zero, each octave weighted by the
    /// one before so the detail gathers on the ridges and the valleys stay smooth. Roughly 0-1.</summary>
    static float Ridged(float x, float y, int octaves)
    {
        float sum = 0f, amp = 1f, norm = 0f, weight = 1f;
        for (int o = 0; o < octaves; o++)
        {
            float n = 1f - Mathf.Abs(Noise(x, y));
            n *= n * weight;
            weight = Mathf.Clamp01(n * 1.5f);
            sum += amp * n;
            norm += amp;
            x = x * 2.03f + 17.1f;
            y = y * 2.03f + 31.7f;
            amp *= 0.5f;
        }
        return sum / norm;
    }

    /// <summary>2D gradient noise, about -1..1.</summary>
    static float Noise(float x, float y)
    {
        int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
        float fx = x - ix, fy = y - iy;
        float u = fx * fx * fx * (fx * (fx * 6f - 15f) + 10f), v = fy * fy * fy * (fy * (fy * 6f - 15f) + 10f);
        float a = Grad(Hash(ix, iy), fx, fy), b = Grad(Hash(ix + 1, iy), fx - 1f, fy);
        float c = Grad(Hash(ix, iy + 1), fx, fy - 1f), d = Grad(Hash(ix + 1, iy + 1), fx - 1f, fy - 1f);
        float ab = a + (b - a) * u, cd = c + (d - c) * u;
        return ab + (cd - ab) * v;
    }

    static uint Hash(int x, int y)
    {
        unchecked
        {
            uint h = (uint)x * 0x8da6b343u ^ (uint)y * 0xd8163841u;
            h ^= h >> 15; h *= 0x2c1b3c6du;
            h ^= h >> 12; h *= 0x297a2d39u;
            h ^= h >> 15;
            return h;
        }
    }

    static float Grad(uint h, float x, float y)
    {
        switch (h & 7)
        {
            case 0: return x + y;
            case 1: return -x + y;
            case 2: return x - y;
            case 3: return -x - y;
            case 4: return x;
            case 5: return -x;
            case 6: return y;
            default: return -y;
        }
    }

    /// <summary>The lower of a and b, rounded off over k where they're close — never above either.</summary>
    static float SoftMin(float a, float b, float k)
    {
        if (k <= 0f) return Mathf.Min(a, b);
        float h = Mathf.Max(k - Mathf.Abs(a - b), 0f) / k;
        return Mathf.Min(a, b) - h * h * k * 0.25f;
    }

    static float SmoothStep01(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }
}
