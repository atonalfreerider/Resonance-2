using UnityEngine;
using UnityEngine.InputSystem;

public class CameraControl : MonoBehaviour
{
    public float Speed = 0.3f;          // Speed of movement and rotation
    public Vector3 center = new Vector3(0,-.9f,0); // Frame the torus and the percussion player below it.
    public Vector3 OrbitState=>new Vector3(rad,alpha,phi);
    public void RestoreOrbit(Vector3 state){rad=state.x;alpha=state.y;phi=state.z;}
    public bool Centered {get;private set;}
    public void AdoptView(Vector3 target,bool centered)
    {
        Centered=centered;
        if(centered)center=target;
        var offset=transform.position-FrameTarget;rad=Mathf.Max(.3f,offset.magnitude);
        alpha=Mathf.Acos(Mathf.Clamp(offset.y/rad,-1,1));phi=Mathf.Atan2(offset.z,offset.x);
    }
    public void OverviewFraming(){Centered=false;center=new Vector3(0,-.9f,0);}
    float rad = 5.6f;
    float alpha = 42f * Mathf.Deg2Rad;
    float phi = 45f * Mathf.Deg2Rad;        // Azimuthal angle (around Y-axis) - set to 45 degrees
    
    public delegate void MovementUpdate();
    public MovementUpdate MovementUpdater;

    void Start()
    {
        UpdateCameraPosition();
        transform.LookAt(FrameTarget);
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
        transform.LookAt(FrameTarget); // Ensure the camera always looks at the center
    }

    public void ResetView() { if(Centered){center=Vector3.zero;rad=4.3f;alpha=42f*Mathf.Deg2Rad;phi=45f*Mathf.Deg2Rad;UpdateCameraPosition();transform.LookAt(FrameTarget);MovementUpdater?.Invoke();return;}center=new Vector3(0,-.9f,0);rad=5.6f; alpha=42f*Mathf.Deg2Rad; phi=45f*Mathf.Deg2Rad; UpdateCameraPosition(); transform.LookAt(FrameTarget); MovementUpdater?.Invoke(); }
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
    // How far the overview aims beside the torus (1 pushes it right, clear of the pattern wheels;
    // 0 centres it when the wheels have their own part of the screen).
    public float SideFrame{get=>sideFrame;set{if(Mathf.Abs(sideFrame-value)<.0005f)return;sideFrame=value;if(!Centered){UpdateCameraPosition();transform.LookAt(FrameTarget);}}}
    float sideFrame=1;
    Vector3 FrameTarget => Centered?center:center + new Vector3(Mathf.Sin(phi),0,-Mathf.Cos(phi))*.95f*sideFrame
        - new Vector3(-Mathf.Cos(alpha)*Mathf.Cos(phi),Mathf.Sin(alpha),-Mathf.Cos(alpha)*Mathf.Sin(phi))*.55f;

    void UpdateCameraPosition()
    {
        // Convert spherical coordinates to Cartesian coordinates
        Vector3 newPosition = FrameTarget + new Vector3(
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
