using UnityEngine;
using UnityEngine.InputSystem;
using Unity.MLAgents;

public class MochiController : MonoBehaviour
{
    public float moveSpeed = 0.35f;   // meters per second
    public float jumpSpeed = 1.5f;    // about 11 cm jump
    public float turnSmooth = 12f;    // higher = turns faster

    // Set by CatAgent: when true, movement comes from the agent instead of the keyboard
    [HideInInspector] public bool externalControl;
    [HideInInspector] public Vector3 externalDir;   // world-space direction, x/z in -1..1

    Rigidbody rb;
    Transform visual;                 // the cat model (first child)
    CatAgent agent;
    Vector3 startPos;
    Quaternion startVisualRot;
    bool jumpRequested;
    Vector3 faceDir;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        agent = GetComponent<CatAgent>();
        visual = transform.GetChild(0);
        startPos = transform.position;
        startVisualRot = visual.rotation;
        faceDir = visual.forward;
    }

    void Update()
    {
        // Jump is keyboard-only, the agent does not jump
        Keyboard k = Keyboard.current;
        if (!externalControl && k != null && k.spaceKey.wasPressedThisFrame) jumpRequested = true;

        // Turn the cat toward where it is moving
        if (faceDir.sqrMagnitude > 0.001f)
        {
            Quaternion target = Quaternion.LookRotation(faceDir, Vector3.up);
            visual.rotation = Quaternion.Slerp(visual.rotation, target, Time.deltaTime * turnSmooth);
        }
    }

    bool IsGrounded()
    {
        return Physics.Raycast(transform.position + Vector3.up * 0.02f, Vector3.down, 0.05f);
    }

    void FixedUpdate()
    {
        Vector3 dir;

        if (externalControl)
        {
            // Agent control (training, inference, or Heuristic Only)
            dir = Vector3.ClampMagnitude(new Vector3(externalDir.x, 0f, externalDir.z), 1f);
        }
        else
        {
            // Manual keyboard control (used only when there is no CatAgent)
            Keyboard k = Keyboard.current;
            if (k == null) return;

            float h = 0f, v = 0f;
            if (k.dKey.isPressed || k.rightArrowKey.isPressed) h += 1f;
            if (k.aKey.isPressed || k.leftArrowKey.isPressed) h -= 1f;
            if (k.wKey.isPressed || k.upArrowKey.isPressed) v += 1f;
            if (k.sKey.isPressed || k.downArrowKey.isPressed) v -= 1f;

            // Camera looks toward -X: up on screen = -X, right on screen = +Z
            dir = new Vector3(-v, 0f, h);
        }

        if (dir.sqrMagnitude > 0.01f) faceDir = dir.normalized;

        Vector3 vel = Vector3.ClampMagnitude(dir * moveSpeed, moveSpeed);
        vel.y = rb.linearVelocity.y;

        if (jumpRequested && IsGrounded()) vel.y = jumpSpeed;
        jumpRequested = false;

        rb.linearVelocity = vel;

        if (transform.position.y < 0.6f)
        {
            // During training the agent ends the episode itself, no game-over screen
            if (agent != null && Academy.IsInitialized && Academy.Instance.IsCommunicatorOn)
                agent.PenaltyFellOff();
            else
                ShapeSpawner.GameOver("Mochi fell off the table!");
        }
    }

    // Called by the spawner when the game restarts
    public void ResetToStart()
    {
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        transform.position = startPos;
        visual.rotation = startVisualRot;
        faceDir = startVisualRot * Vector3.forward;
        jumpRequested = false;
        externalDir = Vector3.zero;
    }
}