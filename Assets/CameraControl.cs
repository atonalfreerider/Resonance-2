using UnityEngine;
using UnityEngine.InputSystem;

public class CameraControl : MonoBehaviour
{
    public float Speed = 0.3f;          // Speed of movement and rotation
    public Vector3 center = new Vector3(0,-.4f,0); // Frame the torus and the percussion player below it.
    float rad = 5.4f;
    float alpha = 65f * Mathf.Deg2Rad;
    float phi = 45f * Mathf.Deg2Rad;        // Azimuthal angle (around Y-axis) - set to 45 degrees
    
    public delegate void MovementUpdate();
    public MovementUpdate MovementUpdater;

    void Start()
    {
        UpdateCameraPosition();
        transform.LookAt(center);
    }

    void Update()
    {
        if (Keyboard.current != null && ExplorerInputFocus.ViewportOwnsKeyboard) MoveCamera();
        var mouse=Mouse.current;
        if(mouse!=null && ExplorerInputFocus.ViewportOwnsKeyboard && ExplorerInputFocus.PointerInViewport)
        {
            bool changed=false;
            if(mouse.rightButton.isPressed)
            {
                Vector2 delta=mouse.delta.ReadValue(); phi-=delta.x*.004f;alpha=Mathf.Clamp(alpha-delta.y*.004f,.05f,Mathf.PI-.05f);changed=delta.sqrMagnitude>0;
            }
            float scroll=mouse.scroll.ReadValue().y;
            if(Mathf.Abs(scroll)>.01f){rad=Mathf.Clamp(rad-scroll*.0015f,.5f,10);changed=true;}
            if(changed){UpdateCameraPosition();MovementUpdater?.Invoke();}
        }
        transform.LookAt(center); // Ensure the camera always looks at the center
    }

    public void ResetView() { center=new Vector3(0,-.4f,0);rad=5.4f; alpha=65f*Mathf.Deg2Rad; phi=45f*Mathf.Deg2Rad; UpdateCameraPosition(); transform.LookAt(center); MovementUpdater?.Invoke(); }
    void MoveCamera()
    {
        bool isMoving = false; // Flag to check if any movement key is pressed

        // Zoom In (W key)
        if (Keyboard.current.upArrowKey.isPressed)
        {
            rad -= Speed * Time.deltaTime;
            rad = Mathf.Max(rad, 0.3f); // Clamp to a minimum radius
            isMoving = true;
        }

        // Zoom Out (S key)
        if (Keyboard.current.downArrowKey.isPressed)
        {
            rad += Speed * Time.deltaTime;
            rad = Mathf.Min(rad, 10f); // Clamp to a maximum radius
            isMoving = true;
        }

        // Orbit Left (A key) - Adjust phi
        if (Keyboard.current.leftArrowKey.isPressed)
        {
            phi += Speed * Time.deltaTime;
            phi = NormalizeAngle(phi);
            isMoving = true;
        }

        // Orbit Right (D key) - Adjust phi
        if (Keyboard.current.rightArrowKey.isPressed)
        {
            phi -= Speed * Time.deltaTime;
            phi = NormalizeAngle(phi);
            isMoving = true;
        }

        // Move Up (E key) - Adjust alpha (towards north pole)
        if (Keyboard.current.pageUpKey.isPressed)
        {
            alpha += Speed * Time.deltaTime;
            alpha = Mathf.Clamp(alpha, 0.01f, Mathf.PI - 0.01f); // Prevent gimbal lock at poles
            isMoving = true;
        }

        // Move Down (Q key) - Adjust alpha (towards south pole)
        if (Keyboard.current.pageDownKey.isPressed)
        {
            alpha -= Speed * Time.deltaTime;
            alpha = Mathf.Clamp(alpha, 0.01f, Mathf.PI - 0.01f); // Prevent gimbal lock at poles
            isMoving = true;
        }

        // Update the camera position based on spherical coordinates
        UpdateCameraPosition();

        // Invoke movement update if any key was pressed
        if (isMoving && MovementUpdater != null)
        {
            MovementUpdater.Invoke();
        }
    }

    /// <summary>
    /// Updates the camera's position based on the current spherical coordinates.
    /// </summary>
    void UpdateCameraPosition()
    {
        // Convert spherical coordinates to Cartesian coordinates
        Vector3 newPosition = center + new Vector3(
            rad * Mathf.Sin(alpha) * Mathf.Cos(phi), // X component
            rad * Mathf.Cos(alpha),                  // Y component
            rad * Mathf.Sin(alpha) * Mathf.Sin(phi)  // Z component
        );

        transform.position = newPosition;
    }

    /// <summary>
    /// Normalizes an angle to the range [-PI, PI].
    /// </summary>
    /// <param name="angle">The angle in radians.</param>
    /// <returns>The normalized angle.</returns>
    static float NormalizeAngle(float angle)
    {
        while (angle > Mathf.PI)
            angle -= 2 * Mathf.PI;
        while (angle < -Mathf.PI)
            angle += 2 * Mathf.PI;
        return angle;
    }
}
