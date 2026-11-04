using UnityEngine;
using UnityEngine.InputSystem;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;

public class CatAgent : Agent
{
    [Header("Outcome rewards")]
    [Tooltip("Shape in the correct hole. Must be clearly larger than the total shaping reward")]
    public float correctReward = 3.0f;
    public float wrongHolePenalty = -1.0f;
    [Tooltip("Same as timeout on purpose: pushing a shape off the desk must not be cheaper than doing nothing")]
    public float shapeFellPenalty = -1.0f;
    public float shapeTimeoutPenalty = -1.0f;
    public float bombPenalty = -1.0f;
    public float catFellPenalty = -1.0f;

    [Header("Reward shaping (reward = scale * distance gained)")]
    [Tooltip("Cat gets closer to the push point behind the shape")]
    public float approachScale = 0.5f;
    [Tooltip("Shape gets closer to its own hole")]
    public float pushScale = 2.0f;
    [Tooltip("Distance behind the shape (opposite to its hole) where the cat should stand, meters")]
    public float pushOffset = 0.12f;
    [Tooltip("Total time penalty if an episode runs until Max Step")]
    public float timePenaltyTotal = 0.5f;

    [Header("Debug")]
    [Tooltip("Show the current episode reward on screen (for the manual Heuristic check)")]
    public bool showDebug = false;

    MochiController mochi;
    Rigidbody catRb;
    Vector3 startPosition;
    Quaternion startRotation;

    Hole[] holes;
    ShapeInfo tracked;
    float prevPushDist, prevShapeHole;

    bool episodeStarted;
    bool endedByEvent;   // true if the episode was ended by one of the events below, false = Max Step

    public override void Initialize()
    {
        catRb = GetComponent<Rigidbody>();
        startPosition = transform.localPosition;
        startRotation = transform.localRotation;

        mochi = GetComponent<MochiController>();
        if (mochi == null)
            Debug.LogError("CatAgent must be on the same object as MochiController");
        else
            mochi.externalControl = true;   // movement now comes from the agent
    }

    public override void OnEpisodeBegin()
    {
        // The previous episode ran out of steps without any event
        if (episodeStarted && !endedByEvent)
        {
            Count("Timeout");
            Academy.Instance.StatsRecorder.Add("Success/Rate", 0f, StatAggregationMethod.Average);
        }
        episodeStarted = true;
        endedByEvent = false;

        transform.localPosition = startPosition;
        transform.localRotation = startRotation;
        if (catRb != null)
        {
            catRb.linearVelocity = Vector3.zero;
            catRb.angularVelocity = Vector3.zero;
        }
        if (mochi != null) mochi.externalDir = Vector3.zero;

        tracked = null;
        ShapeSpawner.ResetWorld();   // remove old shapes/bombs and restart the timers
    }

    // Space Size = 22:
    //   cat position (3) + cat velocity (2)
    //   + shape relative to cat (3) + shape color one-hot (3) + shape to its own hole (3)
    //   + push point relative to cat (3) + shape velocity (2)
    //   + nearest bomb or bomb warning relative to cat (3)
    public override void CollectObservations(VectorSensor sensor)
    {
        sensor.AddObservation(transform.localPosition);
        sensor.AddObservation(Flat2(catRb != null ? catRb.linearVelocity : Vector3.zero));

        ShapeInfo s = NearestShape();
        if (s != null)
        {
            Hole h = GetHole(s.colorIndex);
            Rigidbody srb = s.GetComponent<Rigidbody>();

            sensor.AddObservation(s.transform.position - transform.position);
            sensor.AddOneHotObservation(s.colorIndex, GameColors.All.Length);
            sensor.AddObservation(h != null
                ? h.transform.position - s.transform.position
                : Vector3.zero);
            sensor.AddObservation(Flat(PushPoint(s, h) - transform.position));
            sensor.AddObservation(Flat2(srb != null ? srb.linearVelocity : Vector3.zero));
        }
        else
        {
            sensor.AddObservation(Vector3.zero);
            sensor.AddObservation(new float[GameColors.All.Length]);
            sensor.AddObservation(Vector3.zero);
            sensor.AddObservation(Vector3.zero);
            sensor.AddObservation(Vector2.zero);
        }

        Transform danger = NearestDanger();
        sensor.AddObservation(danger != null ? danger.position - transform.position : Vector3.zero);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        float x = Mathf.Clamp(actions.ContinuousActions[0], -1f, 1f);
        float z = Mathf.Clamp(actions.ContinuousActions[1], -1f, 1f);
        if (mochi != null) mochi.externalDir = new Vector3(x, 0f, z);

        // Shaping: reward the CHANGE in distance, so moving forward then back sums to 0
        ShapeInfo s = NearestShape();
        if (s != null)
        {
            Hole h = GetHole(s.colorIndex);
            float dPush = Flat(PushPoint(s, h) - transform.position).magnitude;
            float dHole = h != null
                ? Flat(s.transform.position - h.transform.position).magnitude
                : 0f;

            // Only reward while we follow the same shape as on the previous step
            if (s == tracked)
            {
                AddReward(approachScale * (prevPushDist - dPush));   // get behind the shape
                AddReward(pushScale * (prevShapeHole - dHole));      // move the shape toward its hole
            }
            tracked = s;
            prevPushDist = dPush;
            prevShapeHole = dHole;
        }
        else
        {
            tracked = null;
        }

        // Small time penalty (guarded: MaxStep = 0 would give -Infinity)
        if (MaxStep > 0) AddReward(-timePenaltyTotal / MaxStep);
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var a = actionsOut.ContinuousActions;
        Keyboard k = Keyboard.current;

        float h = 0f, v = 0f;
        if (k != null)
        {
            if (k.dKey.isPressed || k.rightArrowKey.isPressed) h += 1f;
            if (k.aKey.isPressed || k.leftArrowKey.isPressed) h -= 1f;
            if (k.wKey.isPressed || k.upArrowKey.isPressed) v += 1f;
            if (k.sKey.isPressed || k.downArrowKey.isPressed) v -= 1f;
        }

        // Same screen mapping as MochiController: up = -X, right = +Z
        a[0] = -v;
        a[1] = h;
    }

    // ---------- events (called by Hole, ShapeInfo, Bomb, MochiController) ----------
    public void RewardCorrectSorting() { EndWith("Correct", correctReward); }
    public void PenaltyWrongSorting() { EndWith("WrongHole", wrongHolePenalty); }
    public void PenaltyMissedShape() { EndWith("ShapeFell", shapeFellPenalty); }
    public void PenaltyShapeTimeout() { EndWith("ShapeTimeout", shapeTimeoutPenalty); }
    public void PenaltyHitByBomb() { EndWith("Bomb", bombPenalty); }
    public void PenaltyFellOff() { EndWith("CatFell", catFellPenalty); }

    void EndWith(string key, float reward)
    {
        Count(key);
        // Share of successful episodes (0..1), the main quality metric in TensorBoard
        Academy.Instance.StatsRecorder.Add("Success/Rate", key == "Correct" ? 1f : 0f, StatAggregationMethod.Average);

        endedByEvent = true;
        AddReward(reward);
        EndEpisode();
    }

    // Event counters, shown in TensorBoard under "Events/..."
    void Count(string key)
    {
        Academy.Instance.StatsRecorder.Add("Events/" + key, 1f, StatAggregationMethod.Sum);
    }

    void OnGUI()
    {
        if (!showDebug) return;
        GUI.Label(new Rect(20, 240, 600, 40), "Episode reward: " + GetCumulativeReward().ToString("0.000"));
    }

    // ---------- helpers ----------
    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
    static Vector2 Flat2(Vector3 v) { return new Vector2(v.x, v.z); }

    // The spot right behind the shape, on the opposite side from its hole
    Vector3 PushPoint(ShapeInfo s, Hole h)
    {
        Vector3 p = s.transform.position;
        if (h == null) return p;
        Vector3 toHole = Flat(h.transform.position - p);
        float d = toHole.magnitude;
        if (d < 0.001f) return p;
        return p - toHole / d * pushOffset;
    }

    Hole GetHole(int colorIndex)
    {
        // Holes are created at runtime by ShapeSpawner, so look them up lazily
        if (holes == null || holes.Length == 0)
            holes = FindObjectsByType<Hole>(FindObjectsSortMode.None);
        foreach (var h in holes)
            if (h != null && h.colorIndex == colorIndex) return h;
        return null;
    }

    ShapeInfo NearestShape()
    {
        ShapeInfo best = null;
        float bestD = float.MaxValue;
        foreach (var s in FindObjectsByType<ShapeInfo>(FindObjectsSortMode.None))
        {
            if (s.done) continue;
            float d = (s.transform.position - transform.position).sqrMagnitude;
            if (d < bestD) { bestD = d; best = s; }
        }
        return best;
    }

    // Nearest bomb OR its warning X marker, so the agent gets ~1 s notice before the bomb drops
    Transform NearestDanger()
    {
        Transform best = null;
        float bestD = float.MaxValue;
        foreach (var b in FindObjectsByType<Bomb>(FindObjectsSortMode.None))
        {
            float d = (b.transform.position - transform.position).sqrMagnitude;
            if (d < bestD) { bestD = d; best = b.transform; }
        }
        foreach (var m in FindObjectsByType<BombMarker>(FindObjectsSortMode.None))
        {
            float d = (m.transform.position - transform.position).sqrMagnitude;
            if (d < bestD) { bestD = d; best = m.transform; }
        }
        return best;
    }
}