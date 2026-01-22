using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

public class CameraController : MonoBehaviour
{
    // ===============================
    // INTERNAL STATE (ALWAYS DEFINED)
    // ===============================
    private bool rotatingByTouch = false;
    private Vector2 lastTouchPos;
    private Vector3 mouseWorldPosStart;

    // ===============================
    // REFERENCES
    // ===============================
    public GameObject parentModel;
    public static CameraController Instance { get; private set; }

    // ===============================
    // SETTINGS
    // ===============================
    [Header("Sensitivity Settings")]
    [SerializeField] private float rotationSpeed = 1000f;
    [SerializeField] private float zoomSpeed = 10f;
    [SerializeField] private float panSpeed = 1f;
    [SerializeField] private float touchRotationMultiplier = 0.0025f;

    [Header("Fit View")]
    [SerializeField] private float defaultFieldOfView = 60f;

    // ===============================
    // LIFECYCLE
    // ===============================
    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void Update()
    {
        // Only allow camera control in Builder mode
        if (NavBarController.currentview != NavBarController.View.Building)
            return;

#if UNITY_ANDROID || UNITY_IOS
        HandleTouchCamera();
        return;
#else
        HandleMouseCamera();
#endif
    }

    // ===============================
    // DESKTOP (MOUSE) CONTROLS
    // ===============================
    private void HandleMouseCamera()
    {
        if (Input.GetMouseButton(1))
            OrbitMouse();

        if (Input.GetKey(KeyCode.LeftShift) && Input.GetKeyDown(KeyCode.F))
            FitToScreen();

        if (Input.GetMouseButtonDown(2))
            mouseWorldPosStart = GetPerspectivePos();

        if (Input.GetMouseButton(2))
            PanMouse();

        if (!IsPointerOverInteractiveUI())
            Zoom(Input.GetAxis("Mouse ScrollWheel"));
    }

    private void OrbitMouse()
    {
        float vertical = Input.GetAxis("Mouse Y") * rotationSpeed * Time.deltaTime;
        float horizontal = Input.GetAxis("Mouse X") * rotationSpeed * Time.deltaTime;

        transform.Rotate(Vector3.right, -vertical, Space.Self);
        transform.Rotate(Vector3.up, horizontal, Space.World);
    }

    private void PanMouse()
    {
        float moveX = -Input.GetAxis("Mouse X") * panSpeed;
        float moveY = -Input.GetAxis("Mouse Y") * panSpeed;

        Camera cam = Camera.main;
        cam.transform.position += cam.transform.right * moveX + cam.transform.up * moveY;
    }

    // ===============================
    // TOUCH CONTROLS
    // ===============================
    private void HandleTouchCamera()
    {
        if (ControlManager.Instance != null &&
            ControlManager.Instance.IsDraggingPart)
            return;

#if UNITY_EDITOR
        // ---- Editor: simulate 1-finger touch with mouse ----
        if (Input.GetMouseButtonDown(0))
        {
            rotatingByTouch = true;
            lastTouchPos = Input.mousePosition;
        }
        else if (Input.GetMouseButton(0) && rotatingByTouch)
        {
            Vector2 delta = (Vector2)Input.mousePosition - lastTouchPos;
            lastTouchPos = Input.mousePosition;
            RotateByTouch(delta);
        }
        else if (Input.GetMouseButtonUp(0))
        {
            rotatingByTouch = false;
        }
#else
        // ---- Device ----
        if (Input.touchCount == 1)
        {
            Touch t = Input.GetTouch(0);

            if (EventSystem.current.IsPointerOverGameObject(t.fingerId))
                return;

            if (t.phase == TouchPhase.Began)
            {
                rotatingByTouch = true;
                lastTouchPos = t.position;
            }
            else if (t.phase == TouchPhase.Moved && rotatingByTouch)
            {
                RotateByTouch(t.deltaPosition);
            }
            else if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
            {
                rotatingByTouch = false;
            }
        }
        else if (Input.touchCount == 2)
        {
            HandleTwoFingerPanZoom();
        }
#endif
    }

    private void RotateByTouch(Vector2 delta)
    {
        float speed = rotationSpeed * touchRotationMultiplier;
        transform.Rotate(Vector3.up, delta.x * speed, Space.World);
        transform.Rotate(Vector3.right, -delta.y * speed, Space.Self);
    }

    private void HandleTwoFingerPanZoom()
    {
        Touch t0 = Input.GetTouch(0);
        Touch t1 = Input.GetTouch(1);

        // ---- PAN ----
        Vector2 avgDelta = (t0.deltaPosition + t1.deltaPosition) * 0.5f;
        Vector3 pan =
            (-Camera.main.transform.right * avgDelta.x +
             -Camera.main.transform.up * avgDelta.y) * panSpeed * 0.01f;

        Camera.main.transform.position += pan;

        // ---- ZOOM ----
        Vector2 prev0 = t0.position - t0.deltaPosition;
        Vector2 prev1 = t1.position - t1.deltaPosition;

        float prevDist = Vector2.Distance(prev0, prev1);
        float currDist = Vector2.Distance(t0.position, t1.position);

        float diff = currDist - prevDist;
        Zoom(diff * 0.01f);
    }

    // ===============================
    // SHARED UTILITIES
    // ===============================
    private void Zoom(float zoomDelta)
    {
        if (zoomDelta == 0f || parentModel == null)
            return;

        Camera cam = Camera.main;
        cam.transform.position += cam.transform.forward * zoomDelta * zoomSpeed;

        float dist = Vector3.Distance(cam.transform.position, parentModel.transform.position);
        cam.nearClipPlane = Mathf.Max(0.01f, dist * 0.01f);
        cam.farClipPlane = Mathf.Max(100f, dist * 4f);
    }

    public void FitToScreen()
    {
        if (parentModel == null) return;

        Camera cam = Camera.main;
        cam.fieldOfView = defaultFieldOfView;

        Bounds b = GetBounds(parentModel);
        float radius = b.extents.magnitude;
        float distance = radius / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);

        cam.transform.position = b.center - cam.transform.forward * distance;
        cam.nearClipPlane = Mathf.Max(0.01f, distance * 0.01f);
        cam.farClipPlane = Mathf.Max(100f, distance * 4f);
    }

    private Bounds GetBounds(GameObject root)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
        Bounds b = new Bounds(root.transform.position, Vector3.zero);

        foreach (Renderer r in renderers)
            b.Encapsulate(r.bounds);

        return b;
    }

    private Vector3 GetPerspectivePos()
    {
        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        Plane plane = new Plane(transform.forward, Vector3.zero);
        plane.Raycast(ray, out float dist);
        return ray.GetPoint(dist);
    }

    private bool IsPointerOverInteractiveUI()
    {
        PointerEventData eventData = new PointerEventData(EventSystem.current)
        {
            position = Input.mousePosition
        };

        List<RaycastResult> results = new List<RaycastResult>();
        EventSystem.current.RaycastAll(eventData, results);

        foreach (RaycastResult r in results)
        {
            if (r.gameObject.layer == LayerMask.NameToLayer("UI"))
                return true;
        }
        return false;
    }
}
