using UnityEngine;

// Minimal heads-up readout of the aircraft's current airspeed, bottom-right of the screen.
// Uses OnGUI rather than a Canvas/uGUI Text, so it needs no extra package references.
public class FlightHud : MonoBehaviour
{
    private Rigidbody target;
    private GUIStyle style;

    public void Initialize(Rigidbody aircraftRigidbody)
    {
        target = aircraftRigidbody;
    }

    private void OnGUI()
    {
        if (target == null) return;

        if (style == null)
        {
            style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 28,
                alignment = TextAnchor.LowerRight,
                normal = { textColor = Color.white }
            };
        }

        float speedMs = target.linearVelocity.magnitude;
        float speedKmh = speedMs * 3.6f;

        const float width = 260f;
        const float height = 70f;
        var rect = new Rect(Screen.width - width - 20f, Screen.height - height - 20f, width, height);

        GUI.Label(rect, $"{speedMs:0} m/s\n{speedKmh:0} km/h", style);
    }
}
