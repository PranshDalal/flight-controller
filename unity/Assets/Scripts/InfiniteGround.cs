using UnityEngine;

// Makes the flat landscape effectively endless: the ground plane (and its collider) re-centers
// under the aircraft whenever it drifts far from the middle, and grows to cover everything the
// camera can see. The ground is one flat, uniform color, so moving it is invisible.
//
// The collider is moved by teleporting a static BoxCollider, not with a kinematic Rigidbody:
// a moving kinematic body carries a velocity, and friction would then think the ground is
// sliding along with the aircraft (no braking on a rollout). Teleporting keeps it at rest.
public class InfiniteGround : MonoBehaviour
{
    [Tooltip("The aircraft to keep the ground under.")]
    [SerializeField] private Transform target;

    [Tooltip("Camera whose far clip plane sets how wide the ground must be.")]
    [SerializeField] private Camera viewCamera;

    [Tooltip("Smallest ground width (m), e.g. the original terrain size.")]
    [SerializeField] private float minSize = 4000f;

    // Unity's Plane primitive is 10 x 10 units at scale 1
    private const float PlaneMeshSize = 10f;

    private float _size;

    /// <summary>Wires the target and camera after the bootstrapper has built them.</summary>
    public void Configure(Transform aircraft, Camera cam, float minimumSize)
    {
        target = aircraft;
        viewCamera = cam;
        minSize = minimumSize;

        // swap the plane's mesh collider for a thick box whose top face is the ground (y = 0)
        var meshCollider = GetComponent<MeshCollider>();
        if (meshCollider != null) Destroy(meshCollider);
        var box = gameObject.GetComponent<BoxCollider>();
        if (box == null) box = gameObject.AddComponent<BoxCollider>();
        box.size = new Vector3(PlaneMeshSize, 20f, PlaneMeshSize);
        box.center = new Vector3(0f, -10f, 0f);

        Resize(minSize);
    }

    private void Resize(float size)
    {
        _size = size;
        transform.localScale = new Vector3(size / PlaneMeshSize, 1f, size / PlaneMeshSize);
    }

    private void FixedUpdate()
    {
        if (target == null) return;

        // grow (or shrink) to twice the view distance, so the edge is never on screen
        float needed = Mathf.Max(minSize, 2.2f * (viewCamera != null ? viewCamera.farClipPlane : 0f));
        if (Mathf.Abs(needed - _size) > 0.1f * _size)
            Resize(needed);

        // re-center once the aircraft is a quarter of the way to an edge
        Vector3 offset = target.position - transform.position;
        offset.y = 0f;
        if (offset.sqrMagnitude > (0.25f * _size) * (0.25f * _size))
            transform.position = new Vector3(target.position.x, transform.position.y, target.position.z);
    }
}
