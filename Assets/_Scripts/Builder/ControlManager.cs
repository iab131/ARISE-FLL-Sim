using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using Unity.VisualScripting;

public class ControlManager : MonoBehaviour
{

    public enum TouchOwner
    {
        None,
        Camera,
        Part
    }
    public static TouchOwner CurrentTouchOwner = TouchOwner.None;

    /* =============================================================
     *  HIGH-LEVEL MODES
     * =============================================================*/
    private enum Mode { Move, Holes }
    private Mode currentMode = Mode.Move;

    /* =============================================================
     *  SUB-STATE for "Holes" mode (finite-state machine)
     * =============================================================*/
    private enum HoleState { SelectingFirst, SelectingSecond, PreviewAdjust }
    private HoleState holeState = HoleState.SelectingFirst;

    /* =============================================================
     *  AXIS LOCK for part-dragging
     * =============================================================*/
    private enum Axis { None, X, Y, Z }
    private Axis lockedAxis = Axis.None;

    #if UNITY_IOS || UNITY_ANDROID
private enum TouchIntent
{
    None,
    DragItem,
    RotateCamera
}

private TouchIntent touchIntent = TouchIntent.None;
#endif


    /* =============================================================
     *  CONSTANTS / LAYERS
     * =============================================================*/
    private const int holeLayer = 8;
    private const int partsLayer = 9;
    private const float snapTime = 0.25f;   // seconds for snap animation

    /* =============================================================
     *  HIGHLIGHT RENDERERS & SELECTIONS
     * =============================================================*/
    private Renderer hoverHoleRenderer;
    private Renderer firstHoleRenderer;

    private GameObject firstHoleGO;   // ≙ moving part’s hole
    private GameObject secondHoleGO;  // ≙ target hole
    private Transform hole;

    /* =============================================================
     *  PREVIEW BOOK-KEEPING
     * =============================================================*/
    private Transform movingPartRoot;
    private Vector3 previewOrigPos;
    private Quaternion previewOrigRot;
    private Quaternion previewHoleRot;
    private Vector3 previewHolePos;

    /* =============================================================
     *  GIZMO
     * =============================================================*/
    private Renderer unitRenderer;
    private float unitDistance;


    /* =============================================================
     *  DRAGGING
     * =============================================================*/
    private float dragScreenZ;
    private Vector3 dragOffset;
    private GameObject draggedPart;
    private Plane dragPlane;       // plane we drag on
    private Vector3 grabLocal;

    /* =============================================================
     *  PART SPAWNING
     * =============================================================*/
    public static ControlManager Instance { get; private set; }
    public Transform spawnRoot;
    [SerializeField] private float spawnDistance;
    [SerializeField] private ScrollRect blockPaletteScrollRect;
    private bool isSpawn = false;
    
    [Header("Mode UI")]
    [SerializeField] private Button moveModeButton;
    [SerializeField] private Button holesModeButton;
    public GameObject unitSizeReference; // Assign in Inspector
    public bool IsDraggingPart => draggedPart != null;


    /* ─────────────────────────────────────────────────────────── */
    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        if (moveModeButton != null)
            moveModeButton.onClick.AddListener(MoveMode);

        if (holesModeButton != null)
            holesModeButton.onClick.AddListener(HolesMode);

        UpdateModeButtons();
    }
    private void Start()
    {
        unitRenderer = unitSizeReference.GetComponent<Renderer>();
        unitDistance = unitRenderer.bounds.size.x / 2; // Use X, Y, or Z as needed
    }

    void Update()
    {
        if (IsInputFieldFocused() || MotorLabelManager.Instance.IsModalOpen)
            return;

#if UNITY_IOS || UNITY_ANDRIOD
        if (Application.isMobilePlatform)
        {
            if (currentMode == Mode.Move)
                HandleTouchControls();
            else
                HandleHoleFSM();

            return;
        }
#endif

        // DESKTOP
        HandleModeHotkeys();
        HandleDuplication();
        HandleDeletion();

        if (currentMode == Mode.Move)
        {
            HandleAxisLockHotkeys();
            HandlePartDragging();
        }
        else
        {
            HandleHoleHover();
            HandleHoleFSM();
        }
    }




#if UNITY_IOS || UNITY_ANDROID
    void HandleTouchControls()
    {
        if (Input.touchCount == 0) return;

        Touch t = Input.GetTouch(0);
        int mask = 1 << partsLayer;

        // ───────── TOUCH BEGIN ─────────
        if (t.phase == TouchPhase.Began)
        {
            Ray ray = Camera.main.ScreenPointToRay(t.position);

            if (Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity, mask))
            {
                // ✅ CLAIM TOUCH FOR PART DRAG
                CurrentTouchOwner = TouchOwner.Part;

                draggedPart = GetGroupRoot(hit.transform).gameObject;
                grabLocal = draggedPart.transform.InverseTransformPoint(hit.point);

                // Stable drag plane
                dragPlane = new Plane(Camera.main.transform.forward, hit.point);
            }
            else
            {
                CurrentTouchOwner = TouchOwner.None;
            }
        }

        // ───────── TOUCH MOVE ─────────
        else if ((t.phase == TouchPhase.Moved || t.phase == TouchPhase.Stationary)
                 && draggedPart != null
                 && CurrentTouchOwner == TouchOwner.Part)
        {
            Ray ray = Camera.main.ScreenPointToRay(t.position);

            if (dragPlane.Raycast(ray, out float enter))
            {
                Vector3 planePoint = ray.GetPoint(enter);
                Vector3 grabWorldOffset = draggedPart.transform.TransformVector(grabLocal);
                draggedPart.transform.position = planePoint - grabWorldOffset;
            }
        }

        // ───────── TOUCH END (⬅️ YOUR LINE GOES HERE) ─────────
        else if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
        {
            draggedPart = null;
            CurrentTouchOwner = TouchOwner.None;
        }
    }
#endif


    /* =============================================================
  *  Buttons
  * =============================================================*/
    public void MoveMode()
    {
        currentMode = Mode.Move;
        lockedAxis = Axis.None;
        ClearAllHoleState();
        UpdateModeButtons();
        MotorLabelManager.Instance.SetAssignMode(false);
    }

    public void HolesMode()
    {
        currentMode = Mode.Holes;
        holeState = HoleState.SelectingFirst;
        ClearAllHoleState();
        UpdateModeButtons();
        MotorLabelManager.Instance.SetAssignMode(false);
    }
    private void UpdateModeButtons()
    {
        if (moveModeButton == null || holesModeButton == null) return;

        // quick & clear: active button -> non-interactable + highlighted tint
        bool inMove = currentMode == Mode.Move;

        moveModeButton.interactable = !inMove;
        holesModeButton.interactable = inMove;
    }

    bool IsInputFieldFocused()
    {
        var selected = EventSystem.current.currentSelectedGameObject;
        if (selected == null) return false;
        return selected.GetComponent<TMP_InputField>() != null;
    }

    /* =============================================================
     *  MODE SWITCHES
     * =============================================================*/
    void HandleModeHotkeys()
    {
        if (Input.GetKeyDown(KeyCode.Q))
        {
            MoveMode();
        }
        if (Input.GetKeyDown(KeyCode.W))
        {
            HolesMode();
        }
    }

    /* =============================================================
     *  AXIS-LOCK HOTKEYS
     * =============================================================*/
    void HandleAxisLockHotkeys()
    {
        if (Input.GetKeyDown(KeyCode.X)) lockedAxis = (lockedAxis == Axis.X) ? Axis.None : Axis.X;
        else if (Input.GetKeyDown(KeyCode.Y)) lockedAxis = (lockedAxis == Axis.Y) ? Axis.None : Axis.Y;
        else if (Input.GetKeyDown(KeyCode.Z)) lockedAxis = (lockedAxis == Axis.Z) ? Axis.None : Axis.Z;
        else if (Input.GetKeyDown(KeyCode.Escape)) lockedAxis = Axis.None;
    }

    /* =============================================================
     *  DELETING PARTS
     * =============================================================*/
    void HandleDeletion()
    {
        if (Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Delete))
        {
            GameObject hit = GetObjectUnderCursor(partsLayer);
            if (hit != null)
            {
                if (GetGroupRoot(hit.transform).childCount > 1)
                {
                    if (!hit.name.Contains("ControlHub"))
                    Destroy(hit);
                }
                else
                {
                    if (!HasControlHub(hit.transform))
                    Destroy(GetGroupRoot(hit.transform).gameObject);
                }
            }
        }
    }
    bool IsPointerOverInteractiveUI()
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
    void HandleDuplication()
    {
        if ((Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) &&
            Input.GetKeyDown(KeyCode.D))
        {
            GameObject hit = GetObjectUnderCursor(partsLayer);
            if (hit != null)
            {
                Transform root = GetGroupRoot(hit.transform);
                GameObject duplicate = Instantiate(root.gameObject,root.parent);

                // Calculate bounds
                Bounds bounds = GetTotalBounds(duplicate);
                Vector3 offset = new Vector3(bounds.size.x + .25f, 0f, 0f); // Only X-axis

                duplicate.transform.position = root.position + offset;

            }
        }
    }

    Bounds GetTotalBounds(GameObject go)
    {
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
            return new Bounds(go.transform.position, Vector3.zero);

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }
        return bounds;
    }

    /* =============================================================
     *  PART DRAGGING (unchanged)
     * =============================================================*/
    void HandlePartDragging()
    {
        int mask = 1 << partsLayer;

        if (Input.GetMouseButtonDown(0))
        {
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity, mask))
            {
                // 1. root we will actually move
                draggedPart = GetGroupRoot(hit.transform).gameObject;
                if (draggedPart == null) return;

                // 2. world-space point we clicked on
                Vector3 hitPoint = hit.point;

                // 3. grab point expressed in the group’s local coordinates
                grabLocal = draggedPart.transform.InverseTransformPoint(hitPoint);

                // 4. build a drag plane through that point, facing the camera
                dragPlane = new Plane(Camera.main.transform.forward, hitPoint);
            }
        }

        if (Input.GetMouseButton(0) && draggedPart != null)
        {
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            if (blockPaletteScrollRect != null)
                blockPaletteScrollRect.enabled = false;
            // 1. where the mouse ray meets that plane
            dragPlane.Raycast(ray, out float enter);
            Vector3 planePoint = ray.GetPoint(enter);

            // 2. offset between group origin and grab point (in world space)
            Vector3 grabWorldOffset = draggedPart.transform.TransformVector(grabLocal);

            // 3. desired group position so grab point == planePoint
            Vector3 target = planePoint - grabWorldOffset;

            // optional axis lock
            switch (lockedAxis)
            {
                case Axis.X: target = new Vector3(target.x, draggedPart.transform.position.y, draggedPart.transform.position.z); break;
                case Axis.Y: target = new Vector3(draggedPart.transform.position.x, target.y, draggedPart.transform.position.z); break;
                case Axis.Z: target = new Vector3(draggedPart.transform.position.x, draggedPart.transform.position.y, target.z); break;
            }

            draggedPart.transform.position = target;
        }

        if (Input.GetMouseButtonUp(0))
        {
            draggedPart = null;
            if (blockPaletteScrollRect != null)
                blockPaletteScrollRect.enabled = true;
            if (isSpawn)
            {
                HolesMode();
                isSpawn = false;
            }
        }

    }

    /* =============================================================
     *  HOVER HIGHLIGHT (disabled in PreviewAdjust)
     * =============================================================*/
    void HandleHoleHover()
    {
        if (holeState == HoleState.PreviewAdjust) return;

        GameObject hovered = GetObjectUnderCursor(holeLayer);
        if (hovered != null)
        {
            Renderer rend = hovered.GetComponent<Renderer>();
            if (rend != hoverHoleRenderer)
            {
                if (hoverHoleRenderer != null && hoverHoleRenderer != firstHoleRenderer)
                    hoverHoleRenderer.enabled = false;

                rend.enabled = true;
                hoverHoleRenderer = rend;
            }
        }
        else if (hoverHoleRenderer != null && hoverHoleRenderer != firstHoleRenderer)
        {
            hoverHoleRenderer.enabled = false;
            hoverHoleRenderer = null;
        }
    }

    GameObject GetObjectUnderCursor(int layer)
    {
        int mask = 1 << layer;
        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        return Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity, mask) ? hit.collider.gameObject : null;
    }

    /* =============================================================
     *  HOLE FSM
     * =============================================================*/
    void HandleHoleFSM()
    {
        switch (holeState)
        {
            /* 1. SELECT MOVING PART’S HOLE (first click) */
            case HoleState.SelectingFirst:
                if (IsCancelPressed()) { ClearAllHoleState(); break; }
                if (IsSelectPressed())
                {
                    GameObject hit = GetObjectUnderCursor(holeLayer);
                    if (hit != null)
                    {
                        SetFirstHole(hit);
                        holeState = HoleState.SelectingSecond;
                        TutorialManager.Instance?.SignalStepDone();
                    }
                }
                break;

            /* 2. SELECT TARGET HOLE (second click) */
            case HoleState.SelectingSecond:
                if (IsCancelPressed()) { ClearAllHoleState(); break; }
                if (IsSelectPressed())
                {
                    GameObject hit = GetObjectUnderCursor(holeLayer);
                    if (hit == null || hit == firstHoleGO) break;

                    if (AreComplementary(firstHoleGO.tag, hit.tag) && GetGroupRoot(firstHoleGO.transform) != GetGroupRoot(hit.transform))
                    {
                        secondHoleGO = hit;
                        bool firstIsPeg = IsPeg(firstHoleGO.tag);
                        bool firstIsHole = IsHole(firstHoleGO.tag);
                        bool isAxle = false;
                        // check if axle
                        if (firstHoleGO.CompareTag("Axle") || secondHoleGO.CompareTag("Axle"))
                        {
                            isAxle = true;
                            firstHoleGO = GetAxlePos(firstHoleGO.transform).gameObject;
                            secondHoleGO = GetAxlePos(secondHoleGO.transform).gameObject;
                        }
                        if (HasControlHub(firstHoleGO.transform))
                        {
                            secondHoleGO = firstHoleGO;
                            firstHoleGO = hit;
                        }

                        hole = firstIsHole ? firstHoleGO.transform : secondHoleGO.transform;
                        PreviewSnap(firstHoleGO, secondHoleGO);

                        if (hoverHoleRenderer != null && hoverHoleRenderer != firstHoleRenderer)
                            hoverHoleRenderer.enabled = false;
                        hoverHoleRenderer = null;

                        //gizmo
                        GizmoManager.Instance.ShowHandles(hole.gameObject, firstIsHole, isAxle);
                        ConnectionActionUIManager.Instance?.SetVisible(true);
                        holeState = HoleState.PreviewAdjust;

                        TutorialManager.Instance?.SignalStepDone();
                    }
                    else
                    {
                        SetFirstHole(hit); // restart with new moving part
                    }
                }
                break;

            /* 3. PREVIEW / ROTATE / CONFIRM */
            case HoleState.PreviewAdjust:
                HandlePreviewRotationKeys();

                if (IsCancelPressed()) { CancelPreview(true); ClearAllHoleState(); 
                    GizmoManager.Instance.ClearHandles();
                    ConnectionActionUIManager.Instance?.SetVisible(false);
                    TutorialManager.Instance?.SignalStepDone();
                    break; }
                if (IsConfirmPressed()) { CommitSnap();
                    ConnectionActionUIManager.Instance?.SetVisible(false); 
                    GizmoManager.Instance.ClearHandles();
                    TutorialManager.Instance?.SignalStepDone();
                    break; }

                break;
        }
    }

    /* =============================================================
     *  INPUT UTILS
     * =============================================================*/
    bool IsSelectPressed() => Input.GetMouseButtonDown(0);
    bool IsConfirmPressed()
    {
        return Input.GetKeyDown(KeyCode.Return)
            || Input.GetKeyDown(KeyCode.KeypadEnter)
            || ConnectionActionUIManager.Instance?.ConsumeConfirm() == true;
    }

    bool IsCancelPressed()
    {
        return Input.GetKeyDown(KeyCode.Escape)
            || ConnectionActionUIManager.Instance?.ConsumeCancel() == true;
    }


    /* =============================================================
     *  SELECTION HELPERS
     * =============================================================*/
    void SetFirstHole(GameObject hole)
    {
        if (firstHoleRenderer != null) firstHoleRenderer.enabled = false;
        firstHoleGO = hole;
        firstHoleRenderer = hole.GetComponent<Renderer>();
        if (firstHoleRenderer) firstHoleRenderer.enabled = true;
        //Debug.Log($"Picked moving part: {hole.name} and group {GetGroupRoot(hole.transform).name}");
    }

    /* =============================================================
     *  PREVIEW / COMMIT
     * =============================================================*/
    void PreviewSnap(GameObject holeA, GameObject holeB)
    {
        if (movingPartRoot == null)
        {
            movingPartRoot = GetPartRoot(holeA);   // moving part root
            previewOrigPos = GetGroupRoot(movingPartRoot).position;
            previewOrigRot = GetGroupRoot(movingPartRoot).rotation;
            previewHoleRot = hole.localRotation;
            previewHolePos = hole.localPosition;
        }

        // move part so holeA meets holeB (no parent change)
        SnapPartToHole(GetGroupRoot(movingPartRoot), holeA.transform, holeB.transform, true);
    }

    void CommitSnap()
    {
        Transform moverRoot = movingPartRoot;
        Transform targetRoot = GetPartRoot(secondHoleGO); // stays root

        // final align again (tiny delta) then parent AFTER animation
        SnapPartToHole(
            GetGroupRoot(movingPartRoot),
            firstHoleGO.transform,
            secondHoleGO.transform,
            true,
            () =>
            {
                MergeGroup(moverRoot, targetRoot);
                ClearAllHoleState();
            });
    }

    /* =============================================================
     *  PREVIEW ROTATION / FLIP
     * =============================================================*/
    bool IsPeg(string t) => t == "Peg" || t == "Axle";
    bool IsHole(string t) => t == "PegHole" || t == "AxleHole";

    void HandlePreviewRotationKeys()
    {
        if (firstHoleGO == null || secondHoleGO == null || movingPartRoot == null)
            return;

        
        if (hole != null)
        {

            // make axis move up and down if axle
            if (hole.tag.Contains("Axle") || hole.name.Contains("Axle"))
            {
                if (Input.GetKeyDown(KeyCode.UpArrow))
                {
                    MoveHoleAlongAxis(1);
                }

                if (Input.GetKeyDown(KeyCode.DownArrow))
                {
                    MoveHoleAlongAxis(-1);
                }
            }

            //rotation
            if (Input.GetKeyDown(KeyCode.LeftArrow)) {
                RotateHoleAroundAxis("up", 45f);
                
            }
            if (Input.GetKeyDown(KeyCode.RightArrow)) {
                RotateHoleAroundAxis("up", -45f);
            }
        }

        /* ---- F = end-over-end flip of the PEG / AXLE piece ---- */
        // if (Input.GetKeyDown(KeyCode.F))
        // {
        //     if (hole != null)
        //     {
        //         RotateHoleAroundAxis("right", 180f, true);
                
        //     }
        // }
    }

    public void MoveHoleAlongAxis(int direction)
    {
        Vector3 axis = hole.up;
        hole.position += axis * unitDistance * direction;
        SnapPartToHole(GetGroupRoot(movingPartRoot),
                           firstHoleGO.transform,
                           secondHoleGO.transform,
                           false);
    }

    public void RotateHoleAroundAxis(string type, float angle, bool Animation = false)
    {
        Vector3 pivot = hole.position;
        Vector3 axis = Vector3.up; 

        switch (type.ToLower())
        {
            case "right":
                Transform pegHole = (firstHoleGO.CompareTag("PegHole") || firstHoleGO.CompareTag("AxleHole") || firstHoleGO.name.Contains("Axle"))
                       ? firstHoleGO.transform
                       : (secondHoleGO.CompareTag("PegHole") || secondHoleGO.CompareTag("AxleHole") || secondHoleGO.name.Contains("Axle"))
                         ? secondHoleGO.transform
                         : null;

                pivot = pegHole.position;
                axis = hole.right;
                pegHole.RotateAround(pivot,axis,angle);
               
                break;
            case "up":
                axis = hole.up;
                hole.RotateAround(pivot, axis, angle);

                break;
        }
        print(hole.transform.rotation.eulerAngles);
        SnapPartToHole(GetGroupRoot(movingPartRoot),
                               firstHoleGO.transform,
                               secondHoleGO.transform,
                               Animation);
    }

    /* =============================================================
     *  CANCEL / RESET
     * =============================================================*/
    void ClearAllHoleState()
    {
        GizmoManager.Instance.ClearHandles();
        if (hoverHoleRenderer != null && hoverHoleRenderer != firstHoleRenderer)
            hoverHoleRenderer.enabled = false;
        if (firstHoleRenderer != null) firstHoleRenderer.enabled = false;

        hoverHoleRenderer = null;
        firstHoleRenderer = null;
        firstHoleGO = secondHoleGO = null;
        movingPartRoot = null;
        
        holeState = HoleState.SelectingFirst;
    }

    void CancelPreview(bool clearStored = false)
    {
        if (movingPartRoot != null)
        {
            GetGroupRoot(movingPartRoot).position = previewOrigPos;
            GetGroupRoot(movingPartRoot).rotation = previewOrigRot;
            hole.localRotation = previewHoleRot;
            hole.localPosition = previewHolePos;
            if (clearStored) movingPartRoot = null;
        }
    }

    /* =============================================================
     *  SNAP / ALIGNMENT HELPERS
     * =============================================================*/
    bool AreComplementary(string a, string b)
    {
        return (a == "Axle" && b == "AxleHole") ||
               (a == "AxleHole" && b == "Axle") ||
               (a == "Peg" && b == "PegHole") ||
               (a == "PegHole" && b == "Peg");
    }

    bool HasControlHub(Transform selected)
    {
        Transform group = GetGroupRoot(selected);
        foreach (Transform child in group)
        {
            if (child.name == "ControlHub")
                return true;
        }
        return false;
    }
    Transform GetPartRoot(GameObject holeObj)
    {
        Transform t = holeObj.transform;
        int partsLayer = LayerMask.NameToLayer("Parts");

        while (t != null)
        {
            if (t.gameObject.layer == partsLayer)
                return t;

            t = t.parent;
        }

        return null;
    }


    Transform GetGroupRoot(Transform t)
    {
        for (int i = 0; i < 3 && t.parent != null; i++)
        {
            t = t.parent;
            if (t.name.Contains("Group"))
                return t;
        }

        return t; // No "Group" parent found within 3 levels
    }

    Transform GetAxlePos(Transform root)
    {
        foreach (Transform child in root)
        {
            if (child.name.Contains("AxlePos"))
                return child;
        }

        // No matching child found — return the original root
        return root;
    }

    Transform GetRotatingHub(Transform hole)
    {
        for (int i = 0; i < 4 && hole.parent != null; i++)
        {
            if (hole.gameObject.name == "MotorHub")
                return hole;

            hole = hole.parent;
        }

        return null;
    }

    void SnapPartToHole(
    Transform partRoot, Transform holeA, Transform holeB,
    bool animate, System.Action onComplete = null)
    {
        /* 1 – desired orientation */
        Quaternion targetRot = GetAlignment(holeA, holeB) * partRoot.rotation;

        /* 2 – vector from root → holeA, then rotate that vector */
        Vector3 fromRootToHoleA = holeA.position - partRoot.position;
        Vector3 rotatedOffset = targetRot * Quaternion.Inverse(partRoot.rotation) * fromRootToHoleA;

        /* 3 – place root so rotated holeA lands on holeB */
        Vector3 targetPos = holeB.position - rotatedOffset;

        /* 4 – apply (animate or snap) */
        if (animate)
            StartCoroutine(MovePartSmooth(partRoot, targetPos, targetRot, snapTime, onComplete));
        else
        {
            partRoot.position = targetPos;
            partRoot.rotation = targetRot;
            onComplete?.Invoke();
        }
    }



    IEnumerator MovePartSmooth(
        Transform part, Vector3 tgtPos, Quaternion tgtRot,
        float time, System.Action onComplete)
    {
        Vector3 startPos = part.position;
        Quaternion startRot = part.rotation;
        float t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime / time;
            part.position = Vector3.Lerp(startPos, tgtPos, t);
            part.rotation = Quaternion.Slerp(startRot, tgtRot, t);
            yield return null;
        }
        onComplete?.Invoke();
    }

    Quaternion GetAlignment(Transform holeA, Transform holeB)
    {
        Quaternion from = Quaternion.LookRotation(holeA.forward, holeA.up);
        Quaternion to = Quaternion.LookRotation(holeB.forward, holeB.up);
        return to * Quaternion.Inverse(from);
    }

    /* =============================================================
    *  SPAWN PARTS FROM PART LIST
    * =============================================================*/
    public void StartDraggingSpawnedPart(GameObject prefab)
    {
        Vector3 spawnPos = GetSpawnStartPosition();
        if (currentMode == Mode.Holes)
        {   
            isSpawn = true;
            MoveMode();
        }
        
        // Create an empty container
        GameObject partRoot = new GameObject("Group");
        partRoot.transform.position = spawnPos;
        partRoot.transform.rotation = Quaternion.identity;
        partRoot.transform.SetParent(spawnRoot);

        // Spawn the actual part as a child of the container
        GameObject part = Instantiate(prefab, partRoot.transform.position, Quaternion.identity, partRoot.transform);
        part.transform.localPosition = Vector3.zero;
        part.transform.localRotation = prefab.transform.localRotation;
        part.transform.localScale = new Vector3(1,1,1);
        draggedPart = partRoot;
        CurrentTouchOwner = TouchOwner.Part;
        // pretend they clicked the group’s origin
        Vector3 hitPoint = partRoot.transform.position;

        // local grab point is (0,0,0)
        grabLocal = Vector3.zero;

        // drag plane through that point, facing camera
        dragPlane = new Plane(Camera.main.transform.forward, hitPoint);

    }

    private Vector3 GetSpawnStartPosition()
    {
        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);

        if (Physics.Raycast(ray, out RaycastHit hit))
            return hit.point;

        // ❗ Increase this value to spawn farther from the camera
        return ray.GetPoint(spawnDistance);
    }

    /* =============================================================
    *  SET PARETING, COMBINE PARENTING MOVING
    * =============================================================*/

    void MergeGroup(Transform mover, Transform root)
    {
        Transform rotatingHub = GetRotatingHub(mover);
        Transform group = GetGroupRoot(root);
        if (rotatingHub == null)
        {
            rotatingHub = GetRotatingHub(root);
            group = GetGroupRoot(mover);
        }

        if (rotatingHub == null) //not motor
        { 
            Transform groupA = GetGroupRoot(mover);
            Transform groupB = GetGroupRoot(root);
            Transform target, donor;
            bool AHasHub = HasControlHub(groupA);
            bool BHasHub = HasControlHub(groupB);

            if (AHasHub || BHasHub)  // has control hub, so target is controlhub
            {
                target = AHasHub ? groupA : groupB;
                donor = AHasHub ? groupB : groupA;
            }
            else
            {
                target = (groupA.childCount >= groupB.childCount) ? groupA : groupB;
                donor = (target == groupA) ? groupB : groupA;
            }
            

            // ── Move children from donor → target (keep world pose) ─────
            List<Transform> toMove = new List<Transform>();
            foreach (Transform child in donor) toMove.Add(child);

            foreach (Transform child in toMove)
                child.SetParent(target, true);   // true = keep world position

            Destroy(donor.gameObject);           // remove empty donor

            // ── Re-anchor so first child is at local (0,0,0) ─────────────
            if (target.childCount > 0)
            {
                Transform anchor = target.GetChild(0);
                Vector3 offset = anchor.localPosition;

                foreach (Transform child in target)
                    child.localPosition -= offset;

                target.position += offset;       // keep world position identical
            }
        }
        else
        {
            List<Transform> toMove = new List<Transform>();
            foreach (Transform child in group) toMove.Add(child);

            foreach (Transform child in toMove)
                child.SetParent(rotatingHub, true);
            Destroy(group.gameObject);
        }
    }
}