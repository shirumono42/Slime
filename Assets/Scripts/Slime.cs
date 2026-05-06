using UnityEngine;
using UnityEngine.UI;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class Slime : MonoBehaviour
{
    public enum CursorState
    {
        Outside,
        MembranePush,
        Inside,
        InnerMembranePull
    }

    [Header("Scene References")]
    [SerializeField] private Canvas canvas;
    [SerializeField] private RectTransform customCursor;
    [SerializeField] private RectTransform slimeBody;
    [SerializeField] private CircleCollider2D outerCollider;
    [SerializeField] private CircleCollider2D innerCollider;
    [SerializeField] private Slider viscositySlider;

    [Header("Legacy Tuning")]
    [SerializeField] private float maxOuterOvershoot = 28f;
    [SerializeField] private float outsideCatchupSpeed = 1800f;

    [Header("Pseudo Haptics")]
    [SerializeField] private float membraneThickness = 24f;
    [SerializeField, Range(0.01f, 0.5f)] private float insideViscosity = 0.055f;
    [SerializeField, Range(0.1f, 1f)] private float insideDragMultiplier = 0.2f;
    [SerializeField, Range(0.8f, 1f)] private float outsideFollowRate = 0.98f;
    [SerializeField, Range(0.1f, 1f)] private float breakthroughLerpRate = 0.34f;
    [SerializeField, Range(0.1f, 1f)] private float exitLerpRate = 0.72f;
    [SerializeField, Range(-1f, 1f)] private float pushDotThreshold = 0.45f;
    [SerializeField, Range(-1f, 1f)] private float exitDotThreshold = 0.45f;
    [SerializeField] private float surfaceStickOffset = 6f;
    [SerializeField] private float failedPushReleaseSpeed = 0.32f;
    [SerializeField] private float cursorJitterAmount = 2.2f;
    [SerializeField] private float visualSquashAmount = 0.1f;
    [SerializeField] private Color cursorInsideColor = new Color(0.72f, 0.92f, 1f, 0.86f);

    [Header("Debug")]
    [SerializeField] private CursorState currentRegion;
    [SerializeField] private Vector2 rawMouseCanvasPosition;
    [SerializeField] private Vector2 displayCursorCanvasPosition;
    [SerializeField] private Vector2 realCursorVelocity;

    private RectTransform canvasRect;
    private Image bodyImage;
    private Image cursorImage;
    private Color bodyBaseColor;
    private Color cursorBaseColor;
    private Vector2 previousRealCursorPosition;
    private Vector2 virtualCursorPosition;
    private Vector2 pushStartVirtualPos;
    private Vector2 pushStartRealPos;
    private Vector2 pushDirection;
    private Vector2 invisibleCircleCenter;
    private Vector2 innerInvisibleCircleCenter;
    private float invisibleCircleRadius;
    private float innerInvisibleCircleRadius;
    private Vector2 releaseTarget;
    private float releaseTimer;
    private Vector3 slimeBaseScale = Vector3.one;

    private const float MinMoveForDirection = 0.05f;

    private void Awake()
    {
        if (canvas == null)
        {
            canvas = GetComponentInParent<Canvas>();
        }

        if (customCursor == null)
        {
            GameObject cursorObject = GameObject.Find("CustomCursor");
            if (cursorObject != null)
            {
                customCursor = cursorObject.GetComponent<RectTransform>();
            }
        }

        if (slimeBody == null)
        {
            GameObject slimeObject = GameObject.Find("SlimeBody");
            if (slimeObject != null)
            {
                slimeBody = slimeObject.GetComponent<RectTransform>();
            }
        }

        if (outerCollider == null && slimeBody != null)
        {
            CircleCollider2D[] colliders = slimeBody.GetComponents<CircleCollider2D>();
            if (colliders.Length > 0)
            {
                outerCollider = colliders[0];
                for (int i = 1; i < colliders.Length; i++)
                {
                    if (colliders[i].radius > outerCollider.radius)
                    {
                        outerCollider = colliders[i];
                    }
                }
            }
        }

        if (innerCollider == null && slimeBody != null)
        {
            CircleCollider2D[] colliders = slimeBody.GetComponents<CircleCollider2D>();
            if (colliders.Length > 1)
            {
                innerCollider = colliders[0] == outerCollider ? colliders[1] : colliders[0];
            }
        }

        if (viscositySlider == null)
        {
            viscositySlider = FindAnyObjectByType<Slider>();
        }

        canvasRect = canvas != null ? canvas.transform as RectTransform : null;
        bodyImage = slimeBody != null ? slimeBody.GetComponent<Image>() : null;
        cursorImage = customCursor != null ? customCursor.GetComponent<Image>() : null;
        bodyBaseColor = bodyImage != null ? bodyImage.color : Color.white;
        cursorBaseColor = cursorImage != null ? cursorImage.color : Color.white;
        slimeBaseScale = slimeBody != null ? slimeBody.localScale : Vector3.one;
        customCursor.SetAsLastSibling();

        rawMouseCanvasPosition = GetMouseCanvasPosition();
        previousRealCursorPosition = rawMouseCanvasPosition;
        virtualCursorPosition = customCursor != null ? customCursor.anchoredPosition : rawMouseCanvasPosition;
        displayCursorCanvasPosition = virtualCursorPosition;

        if (viscositySlider != null)
        {
            viscositySlider.minValue = 0.03f;
            viscositySlider.maxValue = 0.18f;
            viscositySlider.SetValueWithoutNotify(insideViscosity);
            viscositySlider.onValueChanged.AddListener(SetInsideViscosity);
        }
    }

    private void OnEnable()
    {
        Cursor.visible = false;
    }

    private void OnDisable()
    {
        Cursor.visible = true;
    }

    private void Update()
    {
        if (canvasRect == null || customCursor == null || outerCollider == null || slimeBody == null)
        {
            return;
        }

        rawMouseCanvasPosition = GetMouseCanvasPosition();
        realCursorVelocity = rawMouseCanvasPosition - previousRealCursorPosition;

        switch (currentRegion)
        {
            case CursorState.Outside:
                UpdateOutside();
                break;
            case CursorState.MembranePush:
                UpdateMembranePush();
                break;
            case CursorState.Inside:
                UpdateInside();
                break;
            case CursorState.InnerMembranePull:
                UpdateInnerMembranePull();
                break;
        }

        displayCursorCanvasPosition = virtualCursorPosition;
        customCursor.SetAsLastSibling();
        customCursor.anchoredPosition = displayCursorCanvasPosition;
        UpdateVisuals();
        previousRealCursorPosition = rawMouseCanvasPosition;
    }

    public void SetInsideViscosity(float value)
    {
        insideViscosity = Mathf.Clamp(value, 0.01f, 0.5f);
    }

    private void UpdateOutside()
    {
        releaseTimer = Mathf.Max(0f, releaseTimer - Time.unscaledDeltaTime);

        float catchupRate = IsVirtualCursorInsideSlime() ? GetEffectiveInsideViscosity() : outsideFollowRate;
        Vector2 catchupPosition = Vector2.Lerp(virtualCursorPosition, rawMouseCanvasPosition, catchupRate);
        virtualCursorPosition = Vector2.MoveTowards(
            catchupPosition,
            rawMouseCanvasPosition,
            outsideCatchupSpeed * Time.unscaledDeltaTime);

        Vector2 slimeCenter = GetSlimeCenter();
        float slimeRadius = GetSlimeRadius();
        float distance = Vector2.Distance(virtualCursorPosition, slimeCenter);
        bool touchingMembrane = Mathf.Abs(distance - slimeRadius) <= membraneThickness || distance < slimeRadius;

        if (touchingMembrane && IsMovingToward(slimeCenter - virtualCursorPosition, pushDotThreshold))
        {
            BeginMembranePush(slimeCenter, slimeRadius);
        }
    }

    private void BeginMembranePush(Vector2 slimeCenter, float slimeRadius)
    {
        currentRegion = CursorState.MembranePush;

        Vector2 fromCenter = virtualCursorPosition - slimeCenter;
        if (fromCenter.sqrMagnitude < 0.001f)
        {
            fromCenter = rawMouseCanvasPosition - slimeCenter;
        }

        pushStartVirtualPos = slimeCenter + fromCenter.normalized * (slimeRadius + surfaceStickOffset);
        pushStartRealPos = rawMouseCanvasPosition;
        pushDirection = (rawMouseCanvasPosition - pushStartVirtualPos).sqrMagnitude > 0.001f
            ? (rawMouseCanvasPosition - pushStartVirtualPos).normalized
            : (slimeCenter - pushStartVirtualPos).normalized;

        invisibleCircleRadius = slimeRadius;
        invisibleCircleCenter = pushStartRealPos + pushDirection * invisibleCircleRadius;
        virtualCursorPosition = pushStartVirtualPos;
    }

    private void UpdateMembranePush()
    {
        Vector2 slimeCenter = GetSlimeCenter();
        float slimeRadius = GetSlimeRadius();
        Vector2 surfaceDirection = (pushStartVirtualPos - slimeCenter).normalized;
        Vector2 surfacePoint = slimeCenter + surfaceDirection * (slimeRadius + surfaceStickOffset);

        float jitter = Mathf.Sin(Time.unscaledTime * 56f) * cursorJitterAmount;
        Vector2 tangent = new Vector2(-surfaceDirection.y, surfaceDirection.x);
        virtualCursorPosition = surfacePoint + tangent * jitter;

        float signedDepth = Vector2.Dot(rawMouseCanvasPosition - invisibleCircleCenter, pushDirection);
        if (signedDepth >= 0f)
        {
            virtualCursorPosition = Vector2.Lerp(virtualCursorPosition, slimeCenter, breakthroughLerpRate);
            BeginInside(rawMouseCanvasPosition);
            return;
        }

        float distanceFromGateCircle = Vector2.Distance(rawMouseCanvasPosition, invisibleCircleCenter);
        bool backedAway = Vector2.Dot(rawMouseCanvasPosition - pushStartRealPos, pushDirection) < -membraneThickness;
        bool slippedOffArc = distanceFromGateCircle > invisibleCircleRadius + maxOuterOvershoot && !IsRealCursorInPushLane();

        if (backedAway || slippedOffArc)
        {
            releaseTarget = ProjectToOuterSurface(rawMouseCanvasPosition, slimeCenter, slimeRadius + surfaceStickOffset);
            currentRegion = CursorState.Outside;
            virtualCursorPosition = Vector2.Lerp(virtualCursorPosition, releaseTarget, failedPushReleaseSpeed);
        }
    }

    private void BeginInside(Vector2 entryRealPosition)
    {
        currentRegion = CursorState.Inside;
        innerInvisibleCircleCenter = entryRealPosition;
        innerInvisibleCircleRadius = GetSlimeRadius() * 2f;
        virtualCursorPosition = Vector2.Lerp(virtualCursorPosition, GetSlimeCenter(), breakthroughLerpRate);
    }

    private void UpdateInside()
    {
        float viscosity = IsVirtualCursorInsideSlime() ? GetEffectiveInsideViscosity() : outsideFollowRate;
        virtualCursorPosition = Vector2.Lerp(virtualCursorPosition, rawMouseCanvasPosition, viscosity);

        Vector2 slimeCenter = GetSlimeCenter();
        float slimeRadius = GetSlimeRadius();
        float distance = Vector2.Distance(virtualCursorPosition, slimeCenter);
        bool nearInnerMembrane = distance >= slimeRadius - membraneThickness;
        Vector2 outward = virtualCursorPosition - slimeCenter;

        if (nearInnerMembrane && IsMovingToward(outward, exitDotThreshold))
        {
            currentRegion = CursorState.InnerMembranePull;
            if (innerInvisibleCircleRadius <= 0f)
            {
                innerInvisibleCircleCenter = rawMouseCanvasPosition;
                innerInvisibleCircleRadius = slimeRadius * 2f;
            }
        }
    }

    private void UpdateInnerMembranePull()
    {
        Vector2 slimeCenter = GetSlimeCenter();
        float slimeRadius = GetSlimeRadius();
        Vector2 fromInnerCenter = rawMouseCanvasPosition - innerInvisibleCircleCenter;
        float realDistance = fromInnerCenter.magnitude;

        if (realDistance >= innerInvisibleCircleRadius)
        {
            virtualCursorPosition = Vector2.Lerp(virtualCursorPosition, rawMouseCanvasPosition, exitLerpRate);
            if (Vector2.Distance(virtualCursorPosition, rawMouseCanvasPosition) < membraneThickness)
            {
                currentRegion = CursorState.Outside;
                releaseTimer = 0.2f;
            }
            return;
        }

        if (realDistance > slimeRadius * 0.35f)
        {
            Vector2 direction = fromInnerCenter.sqrMagnitude > 0.001f ? fromInnerCenter.normalized : (virtualCursorPosition - slimeCenter).normalized;
            virtualCursorPosition = slimeCenter + direction * Mathf.Max(1f, slimeRadius - membraneThickness * 0.5f);
        }
        else
        {
            currentRegion = CursorState.Inside;
        }
    }

    private Vector2 GetMouseCanvasPosition()
    {
        Vector2 screenPosition = GetMouseScreenPosition();
        RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, screenPosition, null, out Vector2 localPosition);
        return localPosition;
    }

    private Vector2 GetMouseScreenPosition()
    {
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null)
        {
            return Mouse.current.position.ReadValue();
        }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.mousePosition;
#else
        return previousRealCursorPosition;
#endif
    }

    private Vector2 GetSlimeCenter()
    {
        Vector2 offset = outerCollider != null ? outerCollider.offset : Vector2.zero;
        Vector2 scale = new Vector2(slimeBody.localScale.x, slimeBody.localScale.y);
        return slimeBody.anchoredPosition + Vector2.Scale(offset, scale);
    }

    private float GetSlimeRadius()
    {
        if (outerCollider == null)
        {
            return 1f;
        }

        float scale = Mathf.Max(Mathf.Abs(slimeBody.localScale.x), Mathf.Abs(slimeBody.localScale.y));
        return outerCollider.radius * scale;
    }

    private Vector2 ProjectToOuterSurface(Vector2 position, Vector2 slimeCenter, float radius)
    {
        Vector2 direction = position - slimeCenter;
        if (direction.sqrMagnitude < 0.001f)
        {
            direction = Vector2.up;
        }

        return slimeCenter + direction.normalized * radius;
    }

    private bool IsMovingToward(Vector2 desiredDirection, float dotThreshold)
    {
        if (realCursorVelocity.sqrMagnitude < MinMoveForDirection || desiredDirection.sqrMagnitude < 0.001f)
        {
            return false;
        }

        float dot = Vector2.Dot(realCursorVelocity.normalized, desiredDirection.normalized);
        return dot > dotThreshold;
    }

    private bool IsVirtualCursorInsideSlime()
    {
        return Vector2.Distance(virtualCursorPosition, GetSlimeCenter()) < GetSlimeRadius();
    }

    private float GetEffectiveInsideViscosity()
    {
        return Mathf.Clamp01(insideViscosity * insideDragMultiplier);
    }

    private bool IsRealCursorInPushLane()
    {
        Vector2 fromStart = rawMouseCanvasPosition - pushStartRealPos;
        if (fromStart.sqrMagnitude < 0.001f)
        {
            return true;
        }

        return Vector2.Dot(fromStart.normalized, pushDirection) > -0.1f;
    }

    private void UpdateVisuals()
    {
        if (bodyImage != null)
        {
            Color targetColor = bodyBaseColor;
            if (currentRegion == CursorState.MembranePush)
            {
                targetColor = Color.Lerp(bodyBaseColor, new Color(0.58f, 0.9f, 0.75f, bodyBaseColor.a), 0.35f);
            }
            else if (currentRegion == CursorState.Inside || currentRegion == CursorState.InnerMembranePull)
            {
                targetColor = Color.Lerp(bodyBaseColor, new Color(0.72f, 1f, 0.88f, bodyBaseColor.a), 0.2f);
            }

            bodyImage.color = Color.Lerp(bodyImage.color, targetColor, 0.18f);
        }

        if (cursorImage != null)
        {
            Color target = IsVirtualCursorInsideSlime() ? cursorInsideColor : cursorBaseColor;
            cursorImage.color = Color.Lerp(cursorImage.color, target, 0.2f);
        }

        if (slimeBody == null)
        {
            return;
        }

        Vector3 targetScale = slimeBaseScale;
        if (currentRegion == CursorState.MembranePush)
        {
            Vector2 center = GetSlimeCenter();
            Vector2 toCursor = (virtualCursorPosition - center).normalized;
            float horizontal = Mathf.Abs(toCursor.x);
            float vertical = Mathf.Abs(toCursor.y);
            targetScale = new Vector3(
                slimeBaseScale.x * (1f + visualSquashAmount * vertical - visualSquashAmount * horizontal),
                slimeBaseScale.y * (1f + visualSquashAmount * horizontal - visualSquashAmount * vertical),
                slimeBaseScale.z);
        }
        else if (currentRegion == CursorState.InnerMembranePull)
        {
            float pulse = Mathf.Sin(Time.unscaledTime * 28f) * visualSquashAmount * 0.35f;
            targetScale = new Vector3(slimeBaseScale.x * (1f + pulse), slimeBaseScale.y * (1f - pulse), slimeBaseScale.z);
        }
        else if (releaseTimer > 0f)
        {
            float pulse = releaseTimer / 0.2f * visualSquashAmount;
            targetScale = new Vector3(slimeBaseScale.x * (1f + pulse), slimeBaseScale.y * (1f - pulse), slimeBaseScale.z);
        }

        slimeBody.localScale = Vector3.Lerp(slimeBody.localScale, targetScale, 0.18f);
    }
}
