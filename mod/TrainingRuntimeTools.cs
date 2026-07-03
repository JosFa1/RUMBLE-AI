using System;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace AI_Train;

internal sealed class TrainingRuntimeHost : MonoBehaviour
{
    public TrainingRuntimeHost(IntPtr ptr) : base(ptr)
    {
    }

    public object RunCoroutine(System.Collections.IEnumerator routine)
    {
        if (routine == null)
        {
            return null;
        }

        var method = typeof(MonoBehaviour)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Name, "StartCoroutine", StringComparison.Ordinal) &&
                candidate.GetParameters().Length == 1 &&
                typeof(System.Collections.IEnumerator).IsAssignableFrom(candidate.GetParameters()[0].ParameterType));

        return method != null ? method.Invoke(this, new object[] { routine }) : null;
    }
}

internal sealed class TrainingProbeCollisionRecorder : MonoBehaviour
{
    public TrainingProbeCollisionRecorder(IntPtr ptr) : base(ptr)
    {
    }

    public int CollisionEnterCount { get; private set; }
    public string LastOtherName { get; private set; }
    public Vector3 LastContactPoint { get; private set; }
    public int TriggerEnterCount { get; private set; }
    public string LastTriggerOtherName { get; private set; }

    public void OnCollisionEnter(Collision collision)
    {
        CollisionEnterCount++;
        LastOtherName = collision?.gameObject != null ? collision.gameObject.name : null;
        if (collision != null && collision.contactCount > 0)
        {
            LastContactPoint = collision.GetContact(0).point;
        }
    }

    public void OnTriggerEnter(Collider other)
    {
        TriggerEnterCount++;
        LastTriggerOtherName = other?.gameObject != null ? other.gameObject.name : null;
    }
}

internal sealed class TrainingMonitorCamera : IDisposable
{
    private readonly Action<string> _logInfo;
    private readonly Action<string> _logWarn;
    private readonly object _gate = new();
    private readonly Vector3 _followOffset = new(0f, 1.9f, -4.75f);

    private GameObject _root;
    private Camera _camera;
    private Transform _followTarget;
    private string _followTargetPath;
    private bool _freeFlyEnabled = true;
    private bool _hasInitialPlacement;
    private float _yaw;
    private float _pitch;
    private Vector3 _initialFreeFlyPosition = new(0f, 2f, -6f);
    private const float MouseSensitivity = 3.0f;
    private const float MoveSpeed = 6.0f;
    private const float BoostMultiplier = 3.5f;
    private const float FollowLerp = 8.0f;
    private const float FollowLookLerp = 12.0f;

    public TrainingMonitorCamera(Action<string> logInfo, Action<string> logWarn)
    {
        _logInfo = logInfo ?? (_ => { });
        _logWarn = logWarn ?? (_ => { });
    }

    public bool FreeFlyEnabled
    {
        get
        {
            lock (_gate)
            {
                return _freeFlyEnabled;
            }
        }
    }

    public void EnsureCreated(string reason = null)
    {
        var created = false;
        lock (_gate)
        {
            if (_root != null)
            {
                return;
            }

            created = true;
            _root = new GameObject("AI_Train_MonitorCamera");
            _root.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
            _root.tag = "MainCamera";
            UnityEngine.Object.DontDestroyOnLoad(_root);

            _camera = _root.AddComponent<Camera>();
            _camera.enabled = true;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.04f, 0.05f, 0.07f, 1f);
            _camera.nearClipPlane = 0.01f;
            _camera.farClipPlane = 2000f;
            _camera.fieldOfView = 60f;
            _camera.depth = 100f;
            _camera.cullingMask = ~0;
            _camera.stereoTargetEye = StereoTargetEyeMask.None;
            _root.transform.position = _initialFreeFlyPosition;
            _root.transform.rotation = Quaternion.Euler(12f, 0f, 0f);
            CacheAnglesFromTransform();
            ApplyCursorState();
        }

        if (created)
        {
            _logInfo($"Training monitor camera created{(string.IsNullOrWhiteSpace(reason) ? string.Empty : $" ({reason})")}.");
        }
    }

    public void ToggleFreeFly(string reason = null)
    {
        EnsureCreated(reason);

        lock (_gate)
        {
            _freeFlyEnabled = !_freeFlyEnabled;
            if (_freeFlyEnabled)
            {
                CacheAnglesFromTransform();
            }
            else
            {
                _hasInitialPlacement = false;
            }

            ApplyCursorState();
        }

        _logInfo($"Training monitor camera mode: {(_freeFlyEnabled ? "free-fly" : "follow")}{(string.IsNullOrWhiteSpace(reason) ? string.Empty : $" ({reason})")}.");
    }

    public void UpdateTarget(GameObject playerRoot)
    {
        EnsureCreated("target-update");

        var followTarget = ResolveFollowTarget(playerRoot);
        lock (_gate)
        {
            if (_followTarget == followTarget)
            {
                return;
            }

            _followTarget = followTarget;
            _followTargetPath = GetPath(followTarget);
            _hasInitialPlacement = false;

            if (_freeFlyEnabled && _camera != null && _followTarget != null)
            {
                var initialPosition = _followTarget.position + (_followTarget.rotation * _followOffset);
                _camera.transform.position = initialPosition;
                _camera.transform.rotation = Quaternion.LookRotation((_followTarget.position + Vector3.up * 0.9f) - initialPosition, Vector3.up);
                CacheAnglesFromTransform();
                _hasInitialPlacement = true;
            }
        }

        if (followTarget != null)
        {
            _logInfo($"Training monitor camera target updated: {_followTargetPath}.");
        }
        else if (_followTargetPath != null)
        {
            _logWarn("Training monitor camera lost its follow target.");
        }
    }

    public void Tick(float deltaTime)
    {
        if (_root == null || _camera == null)
        {
            return;
        }

        lock (_gate)
        {
            if (_freeFlyEnabled)
            {
                TickFreeFly(deltaTime);
            }
            else
            {
                TickFollow(deltaTime);
            }
        }
    }

    public TrainingMonitorCameraState GetState()
    {
        lock (_gate)
        {
            return new TrainingMonitorCameraState
            {
                freeFlyEnabled = _freeFlyEnabled,
                targetFound = _followTarget != null,
                targetPath = _followTargetPath,
                cameraName = _root != null ? _root.name : null,
                cameraPosition = _camera != null ? ToVector3(_camera.transform.position) : null,
                cameraRotation = _camera != null ? ToQuaternion(_camera.transform.rotation) : null
            };
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_root != null)
            {
                try
                {
                    UnityEngine.Object.Destroy(_root);
                }
                catch
                {
                    // Best effort.
                }
            }

            _root = null;
            _camera = null;
            _followTarget = null;
            _followTargetPath = null;
        }
    }

    private void TickFreeFly(float deltaTime)
    {
        if (_camera == null)
        {
            return;
        }

        var mouseX = Input.GetAxisRaw("Mouse X");
        var mouseY = Input.GetAxisRaw("Mouse Y");
        _yaw += mouseX * MouseSensitivity;
        _pitch = Mathf.Clamp(_pitch - (mouseY * MouseSensitivity), -89f, 89f);

        var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        _camera.transform.rotation = rotation;

        var horizontal = 0f;
        if (Input.GetKey(KeyCode.D))
        {
            horizontal += 1f;
        }
        if (Input.GetKey(KeyCode.A))
        {
            horizontal -= 1f;
        }

        var forward = 0f;
        if (Input.GetKey(KeyCode.W))
        {
            forward += 1f;
        }
        if (Input.GetKey(KeyCode.S))
        {
            forward -= 1f;
        }

        var vertical = 0f;
        if (Input.GetKey(KeyCode.E) || Input.GetKey(KeyCode.Space))
        {
            vertical += 1f;
        }
        if (Input.GetKey(KeyCode.Q) || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
        {
            vertical -= 1f;
        }

        var move = new Vector3(horizontal, vertical, forward);
        if (move.sqrMagnitude > 1f)
        {
            move.Normalize();
        }

        var speed = MoveSpeed;
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
        {
            speed *= BoostMultiplier;
        }

        _camera.transform.position += _camera.transform.TransformDirection(move) * (speed * Mathf.Max(deltaTime, 0f));
    }

    private void TickFollow(float deltaTime)
    {
        if (_camera == null)
        {
            return;
        }

        var target = _followTarget;
        if (target == null)
        {
            return;
        }

        if (!_hasInitialPlacement)
        {
            var initialPosition = target.position + (target.rotation * _followOffset);
            _camera.transform.position = initialPosition;
            _camera.transform.rotation = Quaternion.LookRotation((target.position + Vector3.up * 0.9f) - initialPosition, Vector3.up);
            CacheAnglesFromTransform();
            _hasInitialPlacement = true;
        }

        var targetPosition = target.position;
        var targetRotation = target.rotation;
        var desiredPosition = targetPosition + (targetRotation * _followOffset);
        var followT = Mathf.Clamp01(deltaTime * FollowLerp);
        _camera.transform.position = Vector3.Lerp(_camera.transform.position, desiredPosition, followT);

        var lookTarget = targetPosition + Vector3.up * 0.9f;
        var desiredRotation = Quaternion.LookRotation(lookTarget - _camera.transform.position, Vector3.up);
        var lookT = Mathf.Clamp01(deltaTime * FollowLookLerp);
        _camera.transform.rotation = Quaternion.Slerp(_camera.transform.rotation, desiredRotation, lookT);
        CacheAnglesFromTransform();
    }

    private void CacheAnglesFromTransform()
    {
        if (_camera == null)
        {
            return;
        }

        var euler = _camera.transform.rotation.eulerAngles;
        _yaw = NormalizeAngle(euler.y);
        _pitch = NormalizeAngle(euler.x);
    }

    private void ApplyCursorState()
    {
        Cursor.lockState = _freeFlyEnabled ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !_freeFlyEnabled;
    }

    private Transform ResolveFollowTarget(GameObject playerRoot)
    {
        if (playerRoot == null)
        {
            return null;
        }

        var root = playerRoot.transform;
        if (root == null)
        {
            return null;
        }

        var head = TrainingActorLocator.Resolve(root, TrainingActorLocator.HeadCandidates).Transform;
        if (head != null)
        {
            return head;
        }

        return root;
    }

    private static float NormalizeAngle(float angle)
    {
        while (angle > 180f)
        {
            angle -= 360f;
        }

        while (angle < -180f)
        {
            angle += 360f;
        }

        return angle;
    }

    private static string GetPath(Transform transform)
    {
        if (transform == null)
        {
            return string.Empty;
        }

        var parts = new System.Collections.Generic.Stack<string>();
        while (transform != null)
        {
            parts.Push(transform.name);
            transform = transform.parent;
        }

        return string.Join("/", parts);
    }

    private static ObservationVector3 ToVector3(Vector3 value)
    {
        return new ObservationVector3
        {
            x = value.x,
            y = value.y,
            z = value.z
        };
    }

    private static ObservationQuaternion ToQuaternion(Quaternion value)
    {
        return new ObservationQuaternion
        {
            x = value.x,
            y = value.y,
            z = value.z,
            w = value.w
        };
    }
}
