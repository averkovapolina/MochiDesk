using System.Collections;
using UnityEngine;
using Unity.MLAgents;

public static class GameColors
{
    public static readonly Color[] All =
    {
        new Color(0.66f, 0.45f, 0.95f),   // soft purple
        new Color(1f, 0.52f, 0.76f),      // bubblegum pink
        new Color(1f, 0.62f, 0.30f)       // warm orange
    };
    public static readonly string[] Names = { "Purple", "Pink", "Orange" };
}

public class BombMarker : MonoBehaviour { }

public class ShapeInfo : MonoBehaviour
{
    public int colorIndex;
    public bool done;
    public float lostBelowY;
    public float gravityMultiplier = 1f;
    public float slideDamping = 4f;
    public float stuckTimeout = 20f;
    public float timeoutPenalty = -0.5f;
    public Transform mochi;
    public bool slideOffMochi = true;
    public float offMochiRadius = 0.08f;
    public float offMochiSpeed = 0.6f;

    Rigidbody rb;
    float age;

    void Start() { rb = GetComponent<Rigidbody>(); }

    void FixedUpdate()
    {
        if (gravityMultiplier != 1f)
            rb.AddForce(Physics.gravity * (gravityMultiplier - 1f), ForceMode.Acceleration);

        Vector3 v = rb.linearVelocity;
        float k = Mathf.Max(0f, 1f - slideDamping * Time.fixedDeltaTime);
        v.x *= k;
        v.z *= k;

        // Safety net: a shape that comes to rest on top of Mochi slides off sideways
        if (slideOffMochi && mochi != null)
        {
            Vector3 d = transform.position - mochi.position;
            float flat = new Vector2(d.x, d.z).magnitude;
            if (d.y > 0.10f && flat < offMochiRadius)
            {
                Vector3 dir = flat > 0.001f
                    ? new Vector3(d.x, 0f, d.z) / flat
                    : new Vector3(Random.Range(-1f, 1f), 0f, Random.Range(-1f, 1f)).normalized;
                v.x = dir.x * offMochiSpeed;
                v.z = dir.z * offMochiSpeed;
            }
        }

        rb.linearVelocity = v;
    }

    void Update()
    {
        if (done) return;

        if (transform.position.y < lostBelowY)
        {
            done = true;
            ShapeSpawner.Report(-1, "A shape fell off the table (-1)");

            // ML-Agents penalty for missing a shape
            CatAgent agent = FindFirstObjectByType<CatAgent>();
            if (agent != null) agent.PenaltyMissedShape();

            Destroy(gameObject);
            return;
        }

        age += Time.deltaTime;
        if (stuckTimeout > 0f && age > stuckTimeout)
        {
            done = true;
            ShapeSpawner.Report(timeoutPenalty, "Shape timed out (" + timeoutPenalty + ")");

            // Doing nothing must not be a safe option: a timed-out shape ends the episode with a penalty
            CatAgent agent = FindFirstObjectByType<CatAgent>();
            if (agent != null) agent.PenaltyShapeTimeout();

            Destroy(gameObject);
        }
    }
}

public class Hole : MonoBehaviour
{
    public int colorIndex;
    public float radius;

    void OnTriggerStay(Collider other)
    {
        ShapeInfo s = other.GetComponent<ShapeInfo>();
        if (s == null || s.done) return;

        Vector3 d = other.transform.position - transform.position;
        d.y = 0f;
        if (d.magnitude > radius) return;   // what you see is what counts: center inside the colored disc

        s.done = true;

        // ML-Agents reward evaluation
        CatAgent agent = FindFirstObjectByType<CatAgent>();
        if (agent != null)
        {
            if (s.colorIndex == colorIndex)
                agent.RewardCorrectSorting();
            else
                agent.PenaltyWrongSorting();
        }

        if (s.colorIndex == colorIndex)
            ShapeSpawner.Report(1, "Correct! " + GameColors.Names[colorIndex] + " (+1)");
        else
            ShapeSpawner.Report(-1, "Wrong hole! (-1)");

        Destroy(other.gameObject);
    }
}

public class Bomb : MonoBehaviour
{
    public float lifetime = 1.5f;    // Seconds fully visible
    public float fadeTime = 0.5f;    // Seconds it shrinks away at the end

    Collider col;
    Rigidbody rb;
    Renderer spark;
    float t;
    bool touched;

    void Start()
    {
        col = GetComponent<Collider>();
        rb = GetComponent<Rigidbody>();
        Transform s = transform.Find("Spark");
        if (s != null) spark = s.GetComponent<Renderer>();
    }

    void Update()
    {
        t += Time.deltaTime;

        // Blinking spark on the fuse
        if (spark != null) spark.enabled = Mathf.Sin(t * 25f) > -0.3f;

        if (t <= lifetime) return;

        // Shrinking away: no longer touchable, as if it never existed
        if (col.enabled)
        {
            col.enabled = false;
            rb.isKinematic = true;
        }

        float k = Mathf.Clamp01((t - lifetime) / Mathf.Max(0.01f, fadeTime));
        transform.localScale = Vector3.one * (1f - k);
        if (k >= 1f) Destroy(gameObject);
    }

    void OnCollisionEnter(Collision c)
    {
        if (touched || c.rigidbody == null) return;
        if (c.rigidbody.GetComponent<MochiController>() == null) return;

        touched = true;

        // ML-Agents penalty for touching a bomb
        CatAgent agent = FindFirstObjectByType<CatAgent>();
        if (agent != null) agent.PenaltyHitByBomb();

        Destroy(gameObject);
        ShapeSpawner.GameOver("You touched the bomb!");
    }
}

public class ShapeSpawner : MonoBehaviour
{
    [Header("Falling shapes")]
    [Tooltip("Seconds between shape drops at the start of the game")]
    public float interval = 12f;
    [Tooltip("Start height above the desk surface (meters)")]
    public float dropHeight = 0.25f;
    [Tooltip("Starting speed downward (m/s). 0 = just let it fall")]
    public float fallSpeed = 0f;
    [Tooltip("1 = normal gravity, 2 = falls faster, 0.5 = slower")]
    public float gravityMultiplier = 1f;

    [Header("Difficulty ramp (shapes only, bombs are not affected)")]
    [Tooltip("Every this many seconds the shapes start dropping faster")]
    public float rampEverySeconds = 10f;
    [Tooltip("How many seconds are taken off the drop interval each time")]
    public float rampStepSeconds = 1f;
    [Tooltip("The drop interval never goes below this")]
    public float minInterval = 5f;

    [Header("Spawn area")]
    [Tooltip("Distance from the desk edges where nothing spawns (meters)")]
    public float spawnMargin = 0.20f;

    [Header("Object size (meters)")]
    public float minSize = 0.025f;
    public float maxSize = 0.045f;

    [Header("Object behavior")]
    public float mass = 0.05f;
    [Tooltip("Higher = objects stop sooner after a push")]
    public float slideDamping = 4f;
    public float friction = 0.6f;
    [Tooltip("Seconds before an unsorted shape is removed with a penalty. 0 = never")]
    public float stuckTimeout = 30f;
    public float timeoutPenalty = -0.2f;
    [Tooltip("If a shape rests on top of Mochi, it slides off")]
    public bool slideOffMochi = true;
    public float offMochiSpeed = 0.6f;

    [Header("Bomb (own timer, runs at the same time as the shapes)")]
    [Tooltip("Seconds between bombs. 0 = no bombs")]
    public float bombInterval = 4f;
    [Tooltip("Seconds before the first bomb")]
    public float bombFirstDelay = 3f;
    public float bombSize = 0.05f;
    [Tooltip("Seconds the black X shows before the bomb drops")]
    public float bombWarningTime = 1f;
    [Tooltip("Seconds the bomb stays on the desk before it shrinks away")]
    public float bombLifetimeSeconds = 1.5f;
    [Tooltip("Seconds the bomb takes to shrink away at the end")]
    public float bombFadeTime = 0.5f;

    [Header("Game over")]
    [Tooltip("Touching a bomb or Mochi falling shows GAME OVER for this long, then the game restarts")]
    public float gameOverSeconds = 2f;

    [Header("Holes (Corner placement)")]
    [Tooltip("Hole radius in meters (0.07 = 14 cm wide)")]
    public float holeRadius = 0.07f;

    [Header("Desk rim")]
    [Tooltip("Low wall around the desk edge so shapes (and Mochi) cannot fall off")]
    public bool useRim = true;
    public float rimHeight = 0.03f;
    public float rimThickness = 0.01f;

    public static float lastHitTime = -100f;
    public static float score;
    static string message = "";
    static float messageTime = -100f;

    static ShapeSpawner instance;
    static bool gameOver;
    static string gameOverReason = "";
    int generation;

    float currentInterval;
    float shapeTimer;
    float rampTimer;

    float topY, minX, maxX, minZ, maxZ;
    Vector3[] holePositions;   // filled in CreateHoles, used by the curriculum spawn
    Transform mochi;
    MochiController mochiController;

    public static void Report(float points, string text)
    {
        if (gameOver) return;
        score += points;
        message = text;
        messageTime = Time.time;
        Debug.Log(text + "    Score: " + score);
    }

    public static void GameOver(string reason)
    {
        if (gameOver || instance == null) return;

        // During training the agent ends the episode itself: no pause, no game-over screen
        if (Academy.IsInitialized && Academy.Instance.IsCommunicatorOn) return;

        gameOver = true;
        gameOverReason = reason;
        Debug.LogError("GAME OVER: " + reason + "    Final score: " + score);
        instance.StartCoroutine(instance.RestartAfterDelay());
    }

    IEnumerator RestartAfterDelay()
    {
        Time.timeScale = 0f;
        yield return new WaitForSecondsRealtime(gameOverSeconds);

        // EndEpisode triggers OnEpisodeBegin, which calls ResetWorld()
        CatAgent agent = FindFirstObjectByType<CatAgent>();
        if (agent != null) agent.EndEpisode();
        else DoReset();

        gameOver = false;
        Time.timeScale = 1f;
    }

    // Called by CatAgent.OnEpisodeBegin
    public static void ResetWorld()
    {
        if (instance != null) instance.DoReset();
    }

    void DoReset()
    {
        generation++;
        CancelInvoke();

        foreach (ShapeInfo s in FindObjectsByType<ShapeInfo>(FindObjectsSortMode.None))
        {
            s.done = true;   // Destroy happens at the end of the frame, so mark it now
            Destroy(s.gameObject);
        }
        foreach (Bomb b in FindObjectsByType<Bomb>(FindObjectsSortMode.None)) Destroy(b.gameObject);
        foreach (BombMarker m in FindObjectsByType<BombMarker>(FindObjectsSortMode.None)) Destroy(m.gameObject);

        if (mochiController != null) mochiController.ResetToStart();

        score = 0f;
        message = "";
        messageTime = -100f;
        lastHitTime = -100f;

        StartTimers();
    }

    void Start()
    {
        instance = this;
        gameOver = false;
        lastHitTime = -100f;
        score = 0f;
        messageTime = -100f;

        mochiController = FindFirstObjectByType<MochiController>();
        if (mochiController != null) mochi = mochiController.transform;

        Bounds b = GameObject.Find("Desk").GetComponent<Renderer>().bounds;
        topY = b.max.y;

        minX = b.min.x + spawnMargin;
        maxX = b.max.x - spawnMargin;
        minZ = b.min.z + spawnMargin;
        maxZ = b.max.z - spawnMargin;

        CreateHoles(b);
        if (useRim) CreateRim(b);

        StartTimers();
    }

    void OnDestroy()
    {
        Time.timeScale = 1f;
    }

    void StartTimers()
    {
        currentInterval = interval;
        shapeTimer = 1f;
        rampTimer = 0f;

        if (bombInterval > 0f)
            InvokeRepeating(nameof(StartBombDrop), bombFirstDelay, bombInterval);
    }

    void Update()
    {
        if (gameOver) return;

        shapeTimer -= Time.deltaTime;
        if (shapeTimer <= 0f)
        {
            DropShape();
            shapeTimer = currentInterval;
        }

        if (currentInterval > minInterval)
        {
            rampTimer += Time.deltaTime;
            if (rampTimer >= rampEverySeconds)
            {
                rampTimer -= rampEverySeconds;
                currentInterval = Mathf.Max(minInterval, currentInterval - rampStepSeconds);
            }
        }
    }

    void CreateHoles(Bounds b)
    {
        float minXBound = b.min.x + 0.15f;
        float maxXBound = b.max.x - 0.15f;
        float minZBound = b.min.z + 0.15f;
        float maxZBound = b.max.z - 0.15f;

        Vector3[] cornerPositions = new Vector3[]
        {
            new Vector3(minXBound, topY, maxZBound),
            new Vector3(maxXBound, topY, maxZBound),
            new Vector3(maxXBound, topY, minZBound)
        };
        holePositions = cornerPositions;

        for (int i = 0; i < GameColors.All.Length; i++)
        {
            GameObject hole = new GameObject("Hole_" + GameColors.Names[i]);
            hole.transform.position = cornerPositions[i];

            MakeDisc(hole.transform, holeRadius * 1.25f, 0.002f, new Color(0.05f, 0.05f, 0.05f));
            MakeDisc(hole.transform, holeRadius, 0.003f, GameColors.All[i]);

            BoxCollider trig = hole.AddComponent<BoxCollider>();
            trig.isTrigger = true;
            trig.center = new Vector3(0f, 0.03f, 0f);
            trig.size = new Vector3(holeRadius * 2f, 0.06f, holeRadius * 2f);

            Hole h = hole.AddComponent<Hole>();
            h.colorIndex = i;
            h.radius = holeRadius;
        }
    }

    void CreateRim(Bounds b)
    {
        float y = topY + rimHeight / 2f;
        Vector3 c = b.center;
        Vector3 e = b.extents;
        float t = rimThickness;

        MakeWall(new Vector3(c.x - e.x, y, c.z), new Vector3(t, rimHeight, e.z * 2f));
        MakeWall(new Vector3(c.x + e.x, y, c.z), new Vector3(t, rimHeight, e.z * 2f));
        MakeWall(new Vector3(c.x, y, c.z - e.z), new Vector3(e.x * 2f, rimHeight, t));
        MakeWall(new Vector3(c.x, y, c.z + e.z), new Vector3(e.x * 2f, rimHeight, t));
    }

    void MakeWall(Vector3 pos, Vector3 size)
    {
        GameObject w = GameObject.CreatePrimitive(PrimitiveType.Cube);
        w.name = "Rim";
        w.transform.position = pos;
        w.transform.localScale = size;
        w.GetComponent<Renderer>().material.color = new Color(0.45f, 0.30f, 0.15f);
    }

    void MakeDisc(Transform parent, float radius, float height, Color color)
    {
        GameObject d = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Destroy(d.GetComponent<Collider>());
        d.transform.SetParent(parent, false);
        d.transform.localScale = new Vector3(radius * 2f, height / 2f, radius * 2f);
        d.transform.localPosition = new Vector3(0f, height / 2f, 0f);
        d.GetComponent<Renderer>().material.color = color;
    }

    Vector2 RandomSpawnPoint()
    {
        return new Vector2(Random.Range(minX, maxX), Random.Range(minZ, maxZ));
    }

    void DropShape()
    {
        // Curriculum: spawn_radius limits how far from its own hole a shape appears (10 = anywhere)
        float radius = Academy.Instance.EnvironmentParameters.GetWithDefault("spawn_radius", 10f);
        int c = Random.Range(0, GameColors.All.Length);

        Vector2 p = Vector2.zero;
        bool placed = false;

        if (radius < 5f && holePositions != null)
        {
            // Ring around the hole: never closer than twice the hole radius,
            // so a shape cannot fall straight into the hole for free
            Vector3 hp = holePositions[c];
            float minDist = Mathf.Min(holeRadius * 2f, radius * 0.5f);

            for (int i = 0; i < 12 && !placed; i++)
            {
                Vector2 dir = Random.insideUnitCircle.normalized;
                Vector2 cand = new Vector2(hp.x, hp.z) + dir * Random.Range(minDist, radius);
                if (cand.x >= minX && cand.x <= maxX && cand.y >= minZ && cand.y <= maxZ)
                {
                    p = cand;
                    placed = true;
                }
            }
        }

        if (!placed) p = RandomSpawnPoint();   // anywhere on the desk (also the fallback)

        SpawnShape(new Vector3(p.x, topY + dropHeight, p.y), c);
    }

    void SpawnShape(Vector3 pos, int c)
    {
        PrimitiveType[] types = { PrimitiveType.Sphere, PrimitiveType.Cube, PrimitiveType.Capsule };
        GameObject obj = GameObject.CreatePrimitive(types[Random.Range(0, types.Length)]);

        obj.transform.localScale = Vector3.one * Random.Range(minSize, maxSize);
        obj.transform.position = pos;
        obj.transform.rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

        obj.GetComponent<Renderer>().material.color = GameColors.All[c];

        Rigidbody rb = obj.AddComponent<Rigidbody>();
        rb.mass = mass;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.constraints = RigidbodyConstraints.FreezeRotation;
        rb.linearVelocity = Vector3.down * fallSpeed;

        PhysicsMaterial mat = new PhysicsMaterial();
        mat.dynamicFriction = friction;
        mat.staticFriction = friction;
        mat.bounciness = 0f;
        obj.GetComponent<Collider>().material = mat;

        ShapeInfo info = obj.AddComponent<ShapeInfo>();
        info.colorIndex = c;
        info.lostBelowY = topY - 0.15f;
        info.gravityMultiplier = gravityMultiplier;
        info.slideDamping = slideDamping;
        info.stuckTimeout = stuckTimeout;
        info.timeoutPenalty = timeoutPenalty;
        info.mochi = mochi;
        info.slideOffMochi = slideOffMochi;
        info.offMochiSpeed = offMochiSpeed;

        obj.AddComponent<FallingHitDetector>();
    }

    void StartBombDrop() { StartCoroutine(DropBomb()); }

    IEnumerator DropBomb()
    {
        int myGeneration = generation;
        Vector2 p = RandomSpawnPoint();
        GameObject marker = CreateMarker(new Vector3(p.x, topY + 0.005f, p.y), Color.black);
        yield return new WaitForSeconds(bombWarningTime);

        if (myGeneration != generation)
        {
            if (marker != null) Destroy(marker);
            yield break;
        }

        SpawnBomb(new Vector3(p.x, topY + dropHeight, p.y));
        Destroy(marker, 0.3f);
    }

    void SpawnBomb(Vector3 pos)
    {
        GameObject root = new GameObject("Bomb");
        root.transform.position = pos;

        GameObject body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(body.GetComponent<Collider>());
        body.name = "Body";
        body.transform.SetParent(root.transform, false);
        body.transform.localScale = Vector3.one * bombSize;
        body.GetComponent<Renderer>().material.color = new Color(0.08f, 0.08f, 0.1f);

        GameObject fuse = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Destroy(fuse.GetComponent<Collider>());
        fuse.name = "Fuse";
        fuse.transform.SetParent(root.transform, false);
        fuse.transform.localScale = new Vector3(bombSize * 0.12f, bombSize * 0.14f, bombSize * 0.12f);
        fuse.transform.localPosition = new Vector3(0f, bombSize * 0.56f, 0f);
        fuse.GetComponent<Renderer>().material.color = new Color(0.35f, 0.25f, 0.15f);

        GameObject spark = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(spark.GetComponent<Collider>());
        spark.name = "Spark";
        spark.transform.SetParent(root.transform, false);
        spark.transform.localScale = Vector3.one * bombSize * 0.24f;
        spark.transform.localPosition = new Vector3(0f, bombSize * 0.76f, 0f);
        spark.GetComponent<Renderer>().material.color = new Color(1f, 0.55f, 0.1f);

        SphereCollider col = root.AddComponent<SphereCollider>();
        col.radius = bombSize * 0.5f;

        Rigidbody rb = root.AddComponent<Rigidbody>();
        rb.mass = mass;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.constraints = RigidbodyConstraints.FreezeRotation;
        rb.linearDamping = 2f;

        Bomb bomb = root.AddComponent<Bomb>();
        bomb.lifetime = bombLifetimeSeconds;
        bomb.fadeTime = bombFadeTime;
    }

    GameObject CreateMarker(Vector3 pos, Color color)
    {
        GameObject root = new GameObject("BombMarker");
        root.AddComponent<BombMarker>();
        root.transform.position = pos;
        for (int i = 0; i < 2; i++)
        {
            GameObject bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(bar.GetComponent<Collider>());
            bar.transform.SetParent(root.transform, false);
            bar.transform.localScale = new Vector3(0.06f, 0.001f, 0.008f);
            bar.transform.localRotation = Quaternion.Euler(0f, i == 0 ? 45f : -45f, 0f);
            bar.GetComponent<Renderer>().material.color = color;
        }
        return root;
    }

    void OnGUI()
    {
        GUIStyle s = new GUIStyle(GUI.skin.label) { fontSize = 32, fontStyle = FontStyle.Bold };

        if (Time.time - lastHitTime < 2f)
        {
            s.normal.textColor = Color.red;
            GUI.Label(new Rect(20, 20, 900, 50), "ERROR: Mochi was hit!", s);
        }

        s.normal.textColor = Color.white;
        GUI.Label(new Rect(20, 140, 600, 50), "Score: " + score.ToString("0.##"), s);

        GUIStyle info = new GUIStyle(GUI.skin.label) { fontSize = 22 };
        info.normal.textColor = Color.white;
        GUI.Label(new Rect(20, 100, 600, 40), "Shape every " + currentInterval.ToString("0.#") + " s", info);

        if (Time.time - messageTime < 1.5f)
        {
            s.normal.textColor = Color.yellow;
            GUI.Label(new Rect(20, 190, 900, 50), message, s);
        }

        if (gameOver)
        {
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;

            GUIStyle big = new GUIStyle(GUI.skin.label)
            {
                fontSize = 90,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            big.normal.textColor = Color.red;
            GUI.Label(new Rect(0, Screen.height / 2f - 110, Screen.width, 120), "GAME OVER", big);

            GUIStyle small = new GUIStyle(big) { fontSize = 36 };
            small.normal.textColor = Color.white;
            GUI.Label(new Rect(0, Screen.height / 2f + 10, Screen.width, 60), gameOverReason, small);
            GUI.Label(new Rect(0, Screen.height / 2f + 60, Screen.width, 60), "Final score: " + score.ToString("0.##"), small);
        }
    }
}

public class FallingHitDetector : MonoBehaviour
{
    bool reported;

    void OnCollisionEnter(Collision c)
    {
        if (reported || c.rigidbody == null) return;
        if (c.rigidbody.GetComponent<MochiController>() == null) return;

        bool fromAbove = transform.position.y > c.rigidbody.position.y + 0.1f;
        if (fromAbove && c.relativeVelocity.magnitude > 0.5f)
        {
            reported = true;
            Debug.LogError("Mochi was hit by a falling object!");
            ShapeSpawner.lastHitTime = Time.time;
        }
    }
}