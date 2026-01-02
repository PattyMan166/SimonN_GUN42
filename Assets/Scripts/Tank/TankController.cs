using System;
using Cinemachine;
using UnityEngine;
using Zenject;

namespace Tanks
{
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(BaseInputController))]
    public class TankController : MonoBehaviour
    {
        private const float c_convertMeterInSecFromKmInH = 3.6f;

        private Rigidbody _body;
        private BaseInputController _controller;
        
        private Vector3 _prevPosition;
        private float _prevRotation;
        private float _currentSteerAngle;
        
        [Inject]
        private CinemachineVirtualCamera _camera;

        [Header("---References---"), SerializeField]
        [Tooltip("Ссылки на четыре колеса танка")]
        private Wheel[] _wheels = new Wheel[4];
        [SerializeField, Tooltip("Источник звука скольжения шин по поверхности")]
        private AudioSource _skidAudioSource;
        [SerializeField, Tooltip("Графики мощности двигателя на разных передачах\ntime - Speed | value - Torque")]
        private TransmissionSettings _transmission;
        
        [SerializeField, Space, Range(5f, 50f)] 
        private float _maxSteerAngle = 25f;

        [SerializeField, Min(0f)]
        private float _maxHandbrakeTorque = float.MaxValue;
        [SerializeField] 
        private Vector3 _centreOfMass;

        [SerializeField, Tooltip("Дополнительная сила придавливания танка к земле. Улучшает сцепление с трассой")] 
        private float _downforce = 100f;
        [SerializeField, Tooltip("Пороговое значение, при котором скольжение колеса создает эффекты и звуки")] 
        private float _slipLimit = .3f;
        [SerializeField, Tooltip("Множитель мощности двигателя при заднем ходе")]
        private float _reverseMult = .4f;

        [SerializeField, Range(10f, 300f)]
        private float _maxSpeedFOV = 200f;
        [SerializeField]
        private Vector2 _fov = new (40f, 40f);

#if UNITY_EDITOR
        [SerializeField]
        private bool _debugTorque;
#endif
        
        /// <summary>
        /// Текущая скорость танка в горизонтальной плоскости
        /// </summary>
        public float CurrentSpeed { get; private set; }
        /// <summary>
        /// Скорость двигателя, относительно активной передачи
        /// </summary>
        /// <remarks>0 - минимальная скорость передачи | 1 - максимальная скорость передачи</remarks>
        public float EngineSpeed => _transmission != null ? _transmission.EngineSpeed(CurrentSpeed) : 0f;

        private void Start()
        {
            _body = GetComponent<Rigidbody>();
            if (_body == null)
            {
                Debug.LogError("Rigidbody missing on TankController!", this);
                enabled = false;
                return;
            }

            if (_body.isKinematic)
                Debug.LogError("Rigidbody.isKinematic is true — tank won't respond to physics. Set isKinematic = false.", this);
            if (_body.mass <= 0f)
                Debug.LogWarning("Rigidbody mass is <= 0 — consider setting a positive mass.", this);

            _body.centerOfMass = _centreOfMass;

            _controller = GetComponent<BaseInputController>();
            if (_controller == null)
            {
                Debug.LogError("BaseInputController missing on the same GameObject — tank can't receive input.", this);
                enabled = false;
                return;
            }

            _prevPosition = transform.position;

            if (_skidAudioSource == null)
            {
                _skidAudioSource = gameObject.AddComponent<AudioSource>();
                _skidAudioSource.playOnAwake = false;
                _skidAudioSource.loop = true;
                Debug.LogWarning("AudioSource was null: a new AudioSource was added automatically.", this);
            }

            if (_transmission == null)
                Debug.LogError("TransmissionSettings is not assigned in inspector!", this);

            if (_wheels == null || _wheels.Length == 0)
                Debug.LogError("_wheels array is empty or null!", this);
            else
            {
                for (int i = 0; i < _wheels.Length; i++)
                {
                    if (_wheels[i] == null)
                        Debug.LogError($"Wheel at index {i} is null. Fill all wheel references in inspector.", this);
                }
            }

            if (_camera == null)
                Debug.LogWarning("Cinemachine camera not injected. Field of view will not be updated.", this);
        }

        private void FixedUpdate()
        {
            if (_controller == null) return;
            _controller.ManualUpdate();

            var angle = _controller.TankRotate * _maxSteerAngle;

            if (_wheels != null && _wheels.Length >= 2)
            {
                if (_wheels[0] != null) _wheels[0].SteerAngle = angle;
                if (_wheels[1] != null) _wheels[1].SteerAngle = angle;
            }

            CalculateSpeed();
            ApplyDrive();

            AddDownForce();
            CheckForWheelSpin();
        }

        private void CalculateSpeed()
        {
            var position = transform.position;
            position.y = 0f;
            var distance = Vector3.Distance(_prevPosition, position);
            _prevPosition = position;

            if (Time.deltaTime <= 0f) return;
            CurrentSpeed = (float)Math.Round((double)distance / Time.deltaTime * c_convertMeterInSecFromKmInH, 1);

            if (_camera != null)
            {
                _camera.m_Lens.FieldOfView = Mathf.Lerp(_fov.x, _fov.y, Mathf.InverseLerp(0f, _maxSpeedFOV, CurrentSpeed));
            }
        }

        private void ApplyDrive()
        {
            if (_transmission == null)
            {
                Debug.LogError("ApplyDrive called but TransmissionSettings is null. Assign it in inspector.", this);
                return;
            }

            if (_controller == null)
            {
                Debug.LogError("ApplyDrive called but BaseInputController is null.", this);
                return;
            }

            var torque = _controller.Acceleration * _transmission.GetTorque(CurrentSpeed);
            if (_controller.Acceleration < 0f)
                torque *= _reverseMult;
#if UNITY_EDITOR
            if (_debugTorque)
                Debug.Log($"Torque: {torque}");
#endif
            var handbrake = _controller.HandBrake ? _maxHandbrakeTorque : 0f;

            if (_wheels == null || _wheels.Length == 0)
            {
                Debug.LogError("No wheels configured - cannot apply drive.", this);
                return;
            }

            for (int i = 0, iMax = _wheels.Length; i < iMax; i++)
            {
                var w = _wheels[i];
                if (w == null)
                {
                    Debug.LogError($"Wheel at index {i} is null. Skipping.", this);
                    continue;
                }

                // Явные присвоения предотвращают ошибки типов при использовании кортежной распаковки
                w.Torque = torque;
                w.Brake = handbrake;
            }
        }

        private void AddDownForce()
        {
            if (_body == null) return;
            var value = -transform.up * (_downforce * _body.velocity.magnitude);
            _body.AddForce(value);
        }

        private void CheckForWheelSpin()
        {
            if (_wheels == null || _skidAudioSource == null) return;

            var anySlipping = false;
            for (int i = 0, iMax = _wheels.Length; i < iMax; i++)
            {
                var wheel = _wheels[i];
                if (wheel == null) continue;
                var wheelHit = wheel.GetGroundHit;
                if (Mathf.Abs(Mathf.Max(wheelHit.forwardSlip, wheelHit.sidewaysSlip)) >= _slipLimit)
                {
                    anySlipping = true;
                    break;
                }
            }

            if (anySlipping)
            {
                if (!_skidAudioSource.isPlaying) _skidAudioSource.Play();
            }
            else
            {
                if (_skidAudioSource.isPlaying) _skidAudioSource.Stop();
            }
        }

        private void Update()
        {
            if (_wheels == null) return;
            for (int i = 0, iMax = _wheels.Length; i < iMax; i++)
            {
                if (_wheels[i] == null) continue;
                _wheels[i].UpdateVisual();
            }
        }
        
        private void OnDrawGizmos()
        {
            if (!Application.isPlaying)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawSphere(transform.TransformPoint(_centreOfMass), .2f);
            }
        }
    }
}