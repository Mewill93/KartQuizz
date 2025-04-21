using Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using System.Collections;

namespace PUROPORO
{
    public class GoKartController : NetworkBehaviour
    {
        [HideInInspector] public float inputAcceleration;
        [HideInInspector] public float inputSteering;
        [HideInInspector] public float currentSteeringAngle;
        [HideInInspector] public float currentBrakingForce;
        [HideInInspector] public bool isBraking;
        [HideInInspector] public bool isBoosting;
        [HideInInspector] public float currentSpeed;

        public enum Drivetrain { FWD, RWD, AWD };
        public enum Braking { AllWheels, Handbrake };

        public PlayerInputActions playerInputActions;
        private InputAction moveAction;
        private InputAction brakeAction;
        private InputAction useItemAction;
        private InputAction refreshCameraAction;

        [Header("Settings")]
        public Drivetrain drivetrain;
        public Braking brakingSystem;
        public float accelerationForce = 5000f;
        public float brakingForce = 15000f;
        public float maxSteeringAngle = 60f;
        public float maxSpeed = 50f;
        public float reverseSpeedFactor = 0.5f; // Facteur de réduction de la vitesse lors de la marche arrière
        public float minSpeedFactor = 0.5f; // Facteur minimal de réduction de vitesse lors des virages
        public CinemachineVirtualCamera virtualCamera;  // Cinemachine Virtual Camera
        public float dampingX = 1f;
        public float dampingY = 1f;
        public float dampingZ = 1f;

        [Header("Colliders")]
        public WheelCollider wheelColliderFL;
        public WheelCollider wheelColliderFR;
        public WheelCollider wheelColliderRL;
        public WheelCollider wheelColliderRR;

        [Header("Stability Settings")]
        public float antiRollStiffness = 10000f;

        [Header("Item Settings")]
        public float boostMultiplier = 2f;  // Multiplier for the boost effect
        public float boostDuration = 5f;    // Duration of the boost effect
        public GameObject bananaPrefab;
        public GameObject wallPrefab;
        public GameObject itemIconPrefab;  // Prefab for the item icon UI
        public Sprite boostIcon;         // Sprite for boost icon
        public Sprite bananaIcon;        // Sprite for banana icon
        public Sprite wallIcon;          // Sprite for wall icon

        private NetworkVariable<bool> hasBoost = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Owner);
        private NetworkVariable<bool> hasBanana = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Owner);
        private NetworkVariable<bool> hasWall = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Owner);
        private NetworkVariable<int> playerScore = new NetworkVariable<int>(0); // Add this line to track player score

        private SpawnManager spawnManager;
        private Camera cam;
        private Image itemIcon;

        private Rigidbody rb;

        private void Awake()
        {
            playerInputActions = new PlayerInputActions();
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            if (IsOwner)
            {
                // Initialize player position
                spawnManager = GameObject.FindObjectOfType<SpawnManager>();
                if (spawnManager != null)
                {
                    Vector3 randomSpawnPosition = spawnManager.GetRandomSpawnPosition();
                    transform.position = randomSpawnPosition;
                }

                // Enable the virtual camera for this player
                if (virtualCamera != null)
                {
                    virtualCamera.Follow = transform;
                    virtualCamera.LookAt = transform;
                    virtualCamera.gameObject.SetActive(true);
                }
                else
                {
                    Debug.LogWarning("Virtual Camera is not assigned.");
                }

                // Instantiate and setup the item icon
                GameObject itemIconInstance = Instantiate(itemIconPrefab);
                itemIconInstance.transform.SetParent(GameObject.Find("Canvas2").transform, false);
                itemIcon = itemIconInstance.GetComponent<Image>();
                itemIcon.enabled = false;
            }
            else
            {
                if (virtualCamera != null)
                {
                    virtualCamera.gameObject.SetActive(false);
                }

                cam = transform.Find("Camera").GetComponentInChildren<Camera>();
                if (cam != null)
                {
                    cam.targetDisplay = 0;
                    cam.gameObject.SetActive(false); // Ensure other players' cameras are disabled
                }
            }

            moveAction = playerInputActions.Player.Move;
            brakeAction = playerInputActions.Player.Brake;
            useItemAction = playerInputActions.Player.UseItem;
            refreshCameraAction = playerInputActions.Player.RefreshCamera;

            playerInputActions.Enable();
        }



        void Start()
        {
            rb = GetComponent<Rigidbody>();

            if (wheelColliderFL == null || wheelColliderFR == null || wheelColliderRL == null || wheelColliderRR == null)
            {
                Debug.LogError("One or more WheelColliders are not assigned in the inspector");
            }

            if (rb == null)
            {
                Debug.LogError("Rigidbody is not assigned or found");
            }

            AdjustFriction();
            AdjustCenterOfGravity();
        }

        private void FixedUpdate()
        {
            GetInput();
            HandleAcceleration();
            HandleSteering();
            ApplyAntiRoll();
            ApplyGyroscopicStabilization();
            LimitAngularVelocity();
            LimitMaxSpeed();
            UpdateCurrentSpeed();

            if (!IsOwner) return;
            if (useItemAction.triggered)
            {
                if (hasBoost.Value && !isBoosting)
                {
                    ActivateBoostServerRpc();
                }
                else if (hasBanana.Value)
                {
                    DropBananaServerRpc();
                }
                else if (hasWall.Value)
                {
                    PlaceWallServerRpc();
                }
            }
            if (refreshCameraAction.triggered)
            {
                RefreshCamera();
            }
        }

        private void GetInput()
        {
            Vector2 moveInputVector = moveAction.ReadValue<Vector2>();

            float moveInput = moveInputVector.y;
            float turnInput = moveInputVector.x;

            // Si le joueur appuie sur la gâchette gauche ou sur la touche "S" alors que le kart est à l'arrêt ou recule, il passera en marche arrière
            if (moveInput < 0 && currentSpeed <= 0.1f)
            {
                moveInput *= reverseSpeedFactor;
            }

            // Détecter si la gâchette gauche ou la touche de frein est enfoncée
            isBraking = brakeAction.ReadValue<float>() > 0.1f && currentSpeed > 0;

            // Clamp les valeurs pour s'assurer qu'elles restent dans l'intervalle [-1, 1]
            inputAcceleration = Mathf.Clamp(moveInput, -1f, 1f);
            inputSteering = Mathf.Clamp(turnInput, -1f, 1f);
        }

        private void HandleAcceleration()
        {
            float speedFactor = Mathf.Clamp01(1 - (rb.velocity.magnitude / maxSpeed));
            float steeringReduction = Mathf.Clamp01(1 - Mathf.Abs(inputSteering) * 0.5f);
            float adjustedAcceleration = inputAcceleration * speedFactor * Mathf.Max(steeringReduction, minSpeedFactor);

            // Réduire la vitesse de recul
            if (currentSpeed < 0.1f && inputAcceleration < 0)
            {
                adjustedAcceleration *= reverseSpeedFactor;
            }

            switch (drivetrain)
            {
                case Drivetrain.FWD:
                    wheelColliderFL.motorTorque = adjustedAcceleration * accelerationForce;
                    wheelColliderFR.motorTorque = adjustedAcceleration * accelerationForce;
                    break;
                case Drivetrain.RWD:
                    wheelColliderRL.motorTorque = adjustedAcceleration * accelerationForce;
                    wheelColliderRR.motorTorque = adjustedAcceleration * accelerationForce;
                    break;
                case Drivetrain.AWD:
                    wheelColliderFL.motorTorque = adjustedAcceleration * accelerationForce;
                    wheelColliderFR.motorTorque = adjustedAcceleration * accelerationForce;
                    wheelColliderRL.motorTorque = adjustedAcceleration * accelerationForce;
                    wheelColliderRR.motorTorque = adjustedAcceleration * accelerationForce;
                    break;
                default:
                    break;
            }

            ApplyBraking();
        }

        private void UpdateCurrentSpeed()
        {
            currentSpeed = rb.velocity.magnitude;
        }

        private void ApplyBraking()
        {
            if (isBraking)
            {
                rb.velocity *= 0.98f;
                switch (brakingSystem)
                {
                    case Braking.AllWheels:
                        wheelColliderFL.brakeTorque = currentBrakingForce * 100f;
                        wheelColliderFR.brakeTorque = currentBrakingForce * 100f;
                        wheelColliderRL.brakeTorque = currentBrakingForce * 100f;
                        wheelColliderRR.brakeTorque = currentBrakingForce * 100f;
                        break;
                    case Braking.Handbrake:
                        wheelColliderRL.brakeTorque = currentBrakingForce * 100f;
                        wheelColliderRR.brakeTorque = currentBrakingForce * 100f;
                        break;
                    default:
                        break;
                }
                Debug.Log("Frein activé");
            }
            else
            {
                // Relâcher les freins pour permettre la marche arrière
                wheelColliderFL.brakeTorque = 0f;
                wheelColliderFR.brakeTorque = 0f;
                wheelColliderRL.brakeTorque = 0f;
                wheelColliderRR.brakeTorque = 0f;
            }
        }

        private void HandleSteering()
        {
            currentSteeringAngle = maxSteeringAngle * inputSteering;
            wheelColliderFL.steerAngle = currentSteeringAngle;
            wheelColliderFR.steerAngle = currentSteeringAngle;
        }

        private void ApplyAntiRoll()
        {
            WheelHit hit;
            float travelFL = 1.0f;
            float travelFR = 1.0f;
            float travelRL = 1.0f;
            float travelRR = 1.0f;

            bool groundedFL = wheelColliderFL.GetGroundHit(out hit);
            if (groundedFL)
                travelFL = (-wheelColliderFL.transform.InverseTransformPoint(hit.point).y - wheelColliderFL.radius) / wheelColliderFL.suspensionDistance;

            bool groundedFR = wheelColliderFR.GetGroundHit(out hit);
            if (groundedFR)
                travelFR = (-wheelColliderFR.transform.InverseTransformPoint(hit.point).y - wheelColliderFR.radius) / wheelColliderFR.suspensionDistance;

            bool groundedRL = wheelColliderRL.GetGroundHit(out hit);
            if (groundedRL)
                travelRL = (-wheelColliderRL.transform.InverseTransformPoint(hit.point).y - wheelColliderRL.radius) / wheelColliderRL.suspensionDistance;

            bool groundedRR = wheelColliderRR.GetGroundHit(out hit);
            if (groundedRR)
                travelRR = (-wheelColliderRR.transform.InverseTransformPoint(hit.point).y - wheelColliderRR.radius) / wheelColliderRR.suspensionDistance;

            float antiRollForceFront = (travelFL - travelFR) * antiRollStiffness;
            float antiRollForceRear = (travelRL - travelRR) * antiRollStiffness;

            if (groundedFL)
                GetComponent<Rigidbody>().AddForceAtPosition(wheelColliderFL.transform.up * -antiRollForceFront, wheelColliderFL.transform.position);
            if (groundedFR)
                GetComponent<Rigidbody>().AddForceAtPosition(wheelColliderFR.transform.up * antiRollForceFront, wheelColliderFR.transform.position);
            if (groundedRL)
                GetComponent<Rigidbody>().AddForceAtPosition(wheelColliderRL.transform.up * -antiRollForceRear, wheelColliderRL.transform.position);
            if (groundedRR)
                GetComponent<Rigidbody>().AddForceAtPosition(wheelColliderRR.transform.up * antiRollForceRear, wheelColliderRR.transform.position);
        }

        private void ApplyGyroscopicStabilization()
        {
            Vector3 predictedUp = Quaternion.AngleAxis(
                rb.angularVelocity.magnitude * Mathf.Rad2Deg * 0.5f / rb.angularDrag,
                rb.angularVelocity
            ) * transform.up;
            Vector3 torqueVector = Vector3.Cross(predictedUp, Vector3.up);
            rb.AddTorque(torqueVector * rb.mass * rb.angularDrag);
        }

        private void LimitAngularVelocity()
        {
            float maxAngularVelocity = 10f;
            if (rb.angularVelocity.magnitude > maxAngularVelocity)
            {
                rb.angularVelocity = rb.angularVelocity.normalized * maxAngularVelocity;
            }
        }

        private void LimitMaxSpeed()
        {
            if (rb.velocity.magnitude > maxSpeed)
            {
                rb.velocity = rb.velocity.normalized * maxSpeed;
            }
        }

        private void AdjustFriction()
        {
            WheelFrictionCurve forwardFriction = new WheelFrictionCurve();
            forwardFriction.extremumSlip = 0.1f;
            forwardFriction.extremumValue = 1f;
            forwardFriction.asymptoteSlip = 0.3f;
            forwardFriction.asymptoteValue = 0.5f;
            forwardFriction.stiffness = 3f;

            WheelFrictionCurve sidewaysFriction = new WheelFrictionCurve();
            sidewaysFriction.extremumSlip = 0.1f;
            sidewaysFriction.extremumValue = 1f;
            sidewaysFriction.asymptoteSlip = 0.3f;
            sidewaysFriction.asymptoteValue = 0.5f;
            sidewaysFriction.stiffness = 3f;

            wheelColliderFL.forwardFriction = forwardFriction;
            wheelColliderFR.forwardFriction = forwardFriction;
            wheelColliderRL.forwardFriction = forwardFriction;
            wheelColliderRR.forwardFriction = forwardFriction;

            wheelColliderFL.sidewaysFriction = sidewaysFriction;
            wheelColliderFR.sidewaysFriction = sidewaysFriction;
            wheelColliderRL.sidewaysFriction = sidewaysFriction;
            wheelColliderRR.sidewaysFriction = sidewaysFriction;
        }

        private void AdjustCenterOfGravity()
        {
            rb.centerOfMass = new Vector3(0, -0.5f, 0);
        }

        private void OnItemChanged(bool previousValue, bool newValue)
        {
            UpdateItemIcon();
        }

        private void UpdateItemIcon()
        {
            if (itemIcon == null) return;

            itemIcon.enabled = hasBoost.Value || hasBanana.Value || hasWall.Value;

            if (hasBoost.Value)
            {
                itemIcon.sprite = boostIcon;
            }
            else if (hasBanana.Value)
            {
                itemIcon.sprite = bananaIcon;
            }
            else if (hasWall.Value)
            {
                itemIcon.sprite = wallIcon;
            }

            Debug.Log("Item icon updated: " + itemIcon.sprite.name);
        }

        [ServerRpc]
        private void ActivateBoostServerRpc(ServerRpcParams rpcParams = default)
        {
            ActivateBoostClientRpc();
        }

        [ClientRpc]
        private void ActivateBoostClientRpc(ClientRpcParams rpcParams = default)
        {
            if (IsOwner)
            {
                StartCoroutine(BoostCoroutine());
            }
        }

        private IEnumerator BoostCoroutine()
        {
            isBoosting = true;
            hasBoost.Value = false;  // Consumes the boost item
            accelerationForce *= 3;
            maxSpeed *= boostMultiplier;
            yield return new WaitForSeconds(boostDuration);
            accelerationForce /= 3;
            maxSpeed /= boostMultiplier;
            isBoosting = false;
            UpdateItemIcon();
        }

        [ServerRpc]
        private void DropBananaServerRpc(ServerRpcParams rpcParams = default)
        {
            Vector3 dropPosition = transform.position - transform.forward * 2;
            DropBananaClientRpc(dropPosition);
        }

        [ClientRpc]
        private void DropBananaClientRpc(Vector3 position)
        {
            // Adjust the position to be slightly behind the player
            Vector3 dropPosition = position - transform.forward * 2.0f; // 2.0f is the distance behind the player
            dropPosition.y = 0.28f;
            Quaternion dropRotation = Quaternion.Euler(-90, 0, 0);
            Instantiate(bananaPrefab, dropPosition, dropRotation);
            if (IsOwner)
            {
                hasBanana.Value = false;
                UpdateItemIcon();
            }
        }

        [ServerRpc]
        private void PlaceWallServerRpc(ServerRpcParams rpcParams = default)
        {
            Vector3 placePosition = transform.position - transform.forward * 5;
            PlaceWallClientRpc(placePosition);
        }

        [ClientRpc]
        private void PlaceWallClientRpc(Vector3 position)
        {
            Vector3 placePosition = position;
            placePosition.y = 0.28f;
            GameObject wall = Instantiate(wallPrefab, placePosition, transform.rotation);
            StartCoroutine(DestroyWallAfterTime(wall, 6f));
            if (IsOwner)
            {
                hasWall.Value = false;
                UpdateItemIcon();
            }
        }

        public void CollectBoost()
        {
            if (IsOwner)
            {
                CollectBoostServerRpc();
                UpdateItemIcon();
            }
        }

        [ServerRpc]
        private void CollectBoostServerRpc(ServerRpcParams rpcParams = default)
        {
            if (!HasItem())
            {
                hasBoost.Value = true;
            }
        }

        public void CollectBanana()
        {
            if (IsOwner)
            {
                CollectBananaServerRpc();
                UpdateItemIcon();
            }
        }

        [ServerRpc]
        private void CollectBananaServerRpc(ServerRpcParams rpcParams = default)
        {
            if (!HasItem())
            {
                hasBanana.Value = true;
            }
        }

        public void CollectWall()
        {
            if (IsOwner)
            {
                CollectWallServerRpc();
                UpdateItemIcon();
            }
        }

        [ServerRpc]
        private void CollectWallServerRpc(ServerRpcParams rpcParams = default)
        {
            if (!HasItem())
            {
                hasWall.Value = true;
            }
        }

        public bool HasItem()
        {
            return hasBoost.Value || hasBanana.Value || hasWall.Value;
        }

        public void AddScore(int points)
        {
            playerScore.Value += points;
            UpdateScoreClientRpc(playerScore.Value);
        }

        public int GetScore()
        {
            return playerScore.Value;
        }

        [ClientRpc]
        private void UpdateScoreClientRpc(int newScore)
        {
            // Update the player's canvas with the new score
            if (IsOwner)
            {
                var canvas = GameObject.FindObjectOfType<QuestionCanvas>();
                if (canvas != null)
                {
                    canvas.UpdateScore(newScore);
                }
            }
        }

        private IEnumerator DestroyWallAfterTime(GameObject wall, float delay)
        {
            yield return new WaitForSeconds(delay);
            Destroy(wall);
        }

        public void HitByBanana()
        {
            StartCoroutine(BananaHitCoroutine());
        }

        private IEnumerator BananaHitCoroutine()
        {
            float originalSpeed = currentSpeed;
            currentSpeed = 0;  // Stop the car
            for (float t = 0; t < 1f; t += Time.deltaTime)
            {
                transform.Rotate(0, 360 * Time.deltaTime, 0);  // Spin the car
                yield return null;
            }
            currentSpeed = originalSpeed;  // Reset the speed after 1 second
        }

        private void RefreshCamera()
        {
            // Réinitialiser la caméra
            if (virtualCamera != null)
            {
                virtualCamera.gameObject.SetActive(false);
                virtualCamera.gameObject.SetActive(true);
                Debug.Log("Camera refreshed" + virtualCamera.gameObject.name);
            }
            else if (cam != null)
            {
                cam.enabled = false;
                cam.enabled = true;
                Debug.Log("Camera refreshed" + cam.name);
            }

            // Réinitialiser la position du joueur
            ResetPlayerPosition();
        }

        private void ResetPlayerPosition()
        {
            if (spawnManager != null)
            {
                Vector3 randomSpawnPosition = spawnManager.GetRandomSpawnPosition();
                transform.position = randomSpawnPosition;
                rb.velocity = Vector3.zero; // Remettre à zéro la vélocité pour éviter que le joueur continue à se déplacer
                rb.angularVelocity = Vector3.zero; // Remettre à zéro la vélocité angulaire pour éviter une rotation non désirée
                Debug.Log("Player position reset to: " + randomSpawnPosition);
            }
            else
            {
                Debug.LogWarning("SpawnManager is not assigned, unable to reset player position.");
            }
        }
    }
}