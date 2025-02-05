// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using UnityEngine;
using System;
using HoloToolkit.Examples.GazeRuler;
using System.Collections;
using UnityEngine.XR.WSA;

namespace HoloToolkit.Unity.InputModule
{
    /// <summary>
    /// Component that allows dragging an object with your hand on HoloLens.
    /// Dragging is done by calculating the angular delta and z-delta between the current and previous hand positions,
    /// and then repositioning the object based on that.
    /// </summary>
    public class HandDraggable : MonoBehaviour, IFocusable, IInputHandler, ISourceStateHandler
    {
        /// <summary>
        /// Event triggered when dragging starts.
        /// </summary>
        public event Action StartedDragging;

        /// <summary>
        /// Event triggered when dragging stops.
        /// </summary>
        public event Action StoppedDragging;

        [Tooltip("Transform that will be dragged. Defaults to the object of the component.")]
        public Transform HostTransform;

        [Tooltip("Scale by which hand movement in z is multiplied to move the dragged object.")]
        public float DistanceScale = 2f;

        public enum RotationModeEnum
        {
            Default,
            LockObjectRotation,
            OrientTowardUser,
            OrientTowardUserAndKeepUpright
        }

        public RotationModeEnum RotationMode = RotationModeEnum.Default;

        [Tooltip("Controls the speed at which the object will interpolate toward the desired position")]
        [Range(0.01f, 1.0f)]
        public float PositionLerpSpeed = 0.2f;

        [Tooltip("Controls the speed at which the object will interpolate toward the desired rotation")]
        [Range(0.01f, 1.0f)]
        public float RotationLerpSpeed = 0.2f;

        public bool IsDraggingEnabled = true;

        private bool isDragging;
        private bool isGazed;
        private Vector3 objRefForward;
        private Vector3 objRefUp;
        private float objRefDistance;
        private Quaternion gazeAngularOffset;
        private float handRefDistance;
        private Vector3 objRefGrabPoint;

        private Vector3 draggingPosition;
        private Quaternion draggingRotation;

        private IInputSource currentInputSource;
        private uint currentInputSourceId;
        private Rigidbody hostRigidbody;
        private bool hostRigidbodyWasKinematic;


        public bool customColliders = false;
        public bool usePhysics = false;
        public bool anchorPoints = false;
        private WorldAnchor anchor;
        private bool anchorAttached = false;
        private bool onStartDisable = true;
        private void Start()
        {
            if (HostTransform == null)
            {
                HostTransform = transform;
            }

            hostRigidbody = HostTransform.GetComponent<Rigidbody>();


            // On start to want to anchor this point to the world

            
            if(anchorPoints)
            {
                AttachWorldAnchor();
            }
            StoppedDragging += AttachWorldAnchor;
            StartedDragging += RemoveWorldAnchor;
        }

        private void OnDestroy()
        {
            if (isDragging)
            {
                StopDragging();
            }

            if (isGazed)
            {
                OnFocusExit();
            }

            StoppedDragging -= AttachWorldAnchor;
            StartedDragging -= RemoveWorldAnchor;
        }

        private void Update()
        {
            
            if (IsDraggingEnabled && isDragging)
            {
                UpdateDragging();
            }
        }

        /// <summary>
        /// Starts dragging the object.
        /// </summary>
        public void StartDragging(Vector3 initialDraggingPosition)
        {
            if (!IsDraggingEnabled)
            {
                return;
            }

            if (isDragging)
            {
                return;
            }
            
            // TODO: robertes: Fix push/pop and single-handler model so that multiple HandDraggable components
            //       can be active at once.

            // Add self as a modal input handler, to get all inputs during the manipulation
            InputManager.Instance.PushModalInputHandler(gameObject);

            isDragging = true;
            if (hostRigidbody != null)
            {
                hostRigidbodyWasKinematic = hostRigidbody.isKinematic;
                hostRigidbody.isKinematic = true;
            }

            if (usePhysics & hostRigidbody != null)
                hostRigidbody.isKinematic = false;


            Transform cameraTransform = CameraCache.Main.transform;

            Vector3 inputPosition = Vector3.zero;
#if UNITY_2017_2_OR_NEWER
            InteractionSourceInfo sourceKind;
            currentInputSource.TryGetSourceKind(currentInputSourceId, out sourceKind);
            switch (sourceKind)
            {
                case InteractionSourceInfo.Hand:
                    currentInputSource.TryGetGripPosition(currentInputSourceId, out inputPosition);
                    break;
                case InteractionSourceInfo.Controller:
                    currentInputSource.TryGetPointerPosition(currentInputSourceId, out inputPosition);
                    break;
            }
#else
            currentInputSource.TryGetPointerPosition(currentInputSourceId, out inputPosition);
#endif

            Vector3 pivotPosition = GetHandPivotPosition(cameraTransform);
            handRefDistance = Vector3.Magnitude(inputPosition - pivotPosition);
            objRefDistance = Vector3.Magnitude(initialDraggingPosition - pivotPosition);

            Vector3 objForward = HostTransform.forward;
            Vector3 objUp = HostTransform.up;
            // Store where the object was grabbed from
            objRefGrabPoint = cameraTransform.transform.InverseTransformDirection(HostTransform.position - initialDraggingPosition);

            Vector3 objDirection = Vector3.Normalize(initialDraggingPosition - pivotPosition);
            Vector3 handDirection = Vector3.Normalize(inputPosition - pivotPosition);

            objForward = cameraTransform.InverseTransformDirection(objForward);       // in camera space
            objUp = cameraTransform.InverseTransformDirection(objUp);                 // in camera space
            objDirection = cameraTransform.InverseTransformDirection(objDirection);   // in camera space
            handDirection = cameraTransform.InverseTransformDirection(handDirection); // in camera space

            objRefForward = objForward;
            objRefUp = objUp;

            // Store the initial offset between the hand and the object, so that we can consider it when dragging
            gazeAngularOffset = Quaternion.FromToRotation(handDirection, objDirection);
            draggingPosition = initialDraggingPosition;

            // Jon Mod
            if (customColliders)
                SwapCollidersOnDrag(true);

            

            StartedDragging.RaiseEvent();

            

        }

        /// <summary>
        /// Gets the pivot position for the hand, which is approximated to the base of the neck.
        /// </summary>
        /// <returns>Pivot position for the hand.</returns>
        private Vector3 GetHandPivotPosition(Transform cameraTransform)
        {
            return cameraTransform.position + new Vector3(0, -0.2f, 0) - cameraTransform.forward * 0.2f; // a bit lower and behind
        }

        /// <summary>
        /// Enables or disables dragging.
        /// </summary>
        /// <param name="isEnabled">Indicates whether dragging should be enabled or disabled.</param>
        public void SetDragging(bool isEnabled)
        {
            if (IsDraggingEnabled == isEnabled)
            {
                return;
            }

            IsDraggingEnabled = isEnabled;

            if (isDragging)
            {
                StopDragging();
            }
        }

        /// <summary>
        /// Update the position of the object being dragged.
        /// </summary>
        private void UpdateDragging()
        {
            if (usePhysics & hostRigidbody != null)
                hostRigidbody.isKinematic = false;

            Transform cameraTransform = CameraCache.Main.transform;

            Vector3 inputPosition = Vector3.zero;
#if UNITY_2017_2_OR_NEWER
            InteractionSourceInfo sourceKind;
            currentInputSource.TryGetSourceKind(currentInputSourceId, out sourceKind);
            switch (sourceKind)
            {
                case InteractionSourceInfo.Hand:
                    currentInputSource.TryGetGripPosition(currentInputSourceId, out inputPosition);
                    break;
                case InteractionSourceInfo.Controller:
                    currentInputSource.TryGetPointerPosition(currentInputSourceId, out inputPosition);
                    break;
            }
#else
            currentInputSource.TryGetPointerPosition(currentInputSourceId, out inputPosition);
#endif

            Vector3 pivotPosition = GetHandPivotPosition(cameraTransform);

            Vector3 newHandDirection = Vector3.Normalize(inputPosition - pivotPosition);

            newHandDirection = cameraTransform.InverseTransformDirection(newHandDirection); // in camera space
            Vector3 targetDirection = Vector3.Normalize(gazeAngularOffset * newHandDirection);
            targetDirection = cameraTransform.TransformDirection(targetDirection); // back to world space

            float currentHandDistance = Vector3.Magnitude(inputPosition - pivotPosition);

            float distanceRatio = currentHandDistance / handRefDistance;
            float distanceOffset = distanceRatio > 0 ? (distanceRatio - 1f) * DistanceScale : 0;
            float targetDistance = objRefDistance + distanceOffset;

            draggingPosition = pivotPosition + (targetDirection * targetDistance);

            if (RotationMode == RotationModeEnum.OrientTowardUser || RotationMode == RotationModeEnum.OrientTowardUserAndKeepUpright)
            {
                draggingRotation = Quaternion.LookRotation(HostTransform.position - pivotPosition);
            }
            else if (RotationMode == RotationModeEnum.LockObjectRotation)
            {
                draggingRotation = HostTransform.rotation;
            }
            else // RotationModeEnum.Default
            {
                Vector3 objForward = cameraTransform.TransformDirection(objRefForward); // in world space
                Vector3 objUp = cameraTransform.TransformDirection(objRefUp);           // in world space
                draggingRotation = Quaternion.LookRotation(objForward, objUp);
            }

            Vector3 newPosition = Vector3.Lerp(HostTransform.position, draggingPosition + cameraTransform.TransformDirection(objRefGrabPoint), PositionLerpSpeed);
            // Apply Final Position
            if (!usePhysics)
            {
                // original code from mrtk
                if (hostRigidbody == null)
                {
                    HostTransform.position = newPosition;
                }
                else
                {
                    hostRigidbody.MovePosition(newPosition);
                }
            }


            // Apply Final Rotation
            Quaternion newRotation = Quaternion.Lerp(HostTransform.rotation, draggingRotation, RotationLerpSpeed);
            if (hostRigidbody == null)
            {
                HostTransform.rotation = newRotation;
            }
            else
            {
                hostRigidbody.MoveRotation(newRotation);
            }

            if (RotationMode == RotationModeEnum.OrientTowardUserAndKeepUpright)
            {
                Quaternion upRotation = Quaternion.FromToRotation(HostTransform.up, Vector3.up);
                HostTransform.rotation = upRotation * HostTransform.rotation;
            }

            // JON MOD
            if (hostRigidbody != null && usePhysics)
                hostRigidbody.velocity = ((draggingPosition + cameraTransform.TransformDirection(objRefGrabPoint)) - HostTransform.position) * 10;

            // JON MOD
            // Check if you are moving a point to then update the line lengths
            Interact interact = GetComponent<Interact>();
            if (interact && PolygonManager.Instance)
            {
                if (interact.interactObj == Interact.InteractObj.Point)
                {
                    PolygonManager.Instance.UpdateAllPointPositions();
                    PolygonManager.Instance.UpdateLineLength();
                    PolygonManager.Instance.UpdatePolygonMesh();
                }
            }
        }

        /// <summary>
        /// Stops dragging the object.
        /// </summary>
        public void StopDragging()
        {
            if (!isDragging)
            {
                return;
            }

            

            // Remove self as a modal input handler
            InputManager.Instance.PopModalInputHandler();

            isDragging = false;
            currentInputSource = null;
            currentInputSourceId = 0;
            if (hostRigidbody != null)
            {
                hostRigidbody.isKinematic = hostRigidbodyWasKinematic;
            }

            // Jon Mod
            if (customColliders)
                SwapCollidersOnDrag(false);
            if (hostRigidbody != null)
            {
                print("rigidbody exists");
                hostRigidbody.velocity = Vector3.zero;
                hostRigidbody.angularVelocity = Vector3.zero;
            }


            
            StoppedDragging.RaiseEvent();
            
        }

        public void OnFocusEnter()
        {
            
            if (!IsDraggingEnabled)
            {
                return;
            }

            if (isGazed)
            {
                return;
            }
            
            isGazed = true;

        }

        public void OnFocusExit()
        {
            
            if (!IsDraggingEnabled)
            {
                return;
            }

            if (!isGazed)
            {
                return;
            }

            isGazed = false;

        }

        public void OnInputUp(InputEventData eventData)
        {
            if (currentInputSource != null &&
                eventData.SourceId == currentInputSourceId)
            {
                eventData.Use(); // Mark the event as used, so it doesn't fall through to other handlers.

                

                StopDragging();
            }
        }

        public void OnInputDown(InputEventData eventData)
        {
            if (isDragging)
            {
                // We're already handling drag input, so we can't start a new drag operation.
                return;
            }

#if UNITY_2017_2_OR_NEWER
            InteractionSourceInfo sourceKind;
            eventData.InputSource.TryGetSourceKind(eventData.SourceId, out sourceKind);
            if (sourceKind != InteractionSourceInfo.Hand)
            {
                if (!eventData.InputSource.SupportsInputInfo(eventData.SourceId, SupportedInputInfo.GripPosition))
                {
                    // The input source must provide grip positional data for this script to be usable
                    return;
                }
            }
#else
            if (!eventData.InputSource.SupportsInputInfo(eventData.SourceId, SupportedInputInfo.PointerPosition))
            {
                // The input source must provide positional data for this script to be usable
                return;
            }
#endif // UNITY_2017_2_OR_NEWER

            eventData.Use(); // Mark the event as used, so it doesn't fall through to other handlers.

            currentInputSource = eventData.InputSource;
            currentInputSourceId = eventData.SourceId;

            FocusDetails? details = FocusManager.Instance.TryGetFocusDetails(eventData);

            Vector3 initialDraggingPosition = (details == null)
                ? HostTransform.position
                : details.Value.Point;

            

            StartDragging(initialDraggingPosition);
        }

        public void OnSourceDetected(SourceStateEventData eventData)
        {
            // Nothing to do
            
        }

        public void OnSourceLost(SourceStateEventData eventData)
        {
            if (currentInputSource != null && eventData.SourceId == currentInputSourceId)
            {
                StopDragging();                
            }
        }

        // I had a box collider so that when you are moving your point it can get closer to the mesh/spatial mapping
        private void SwapCollidersOnDrag(bool enable)
        {
            if (enable)
            {
                GetComponent<BoxCollider>().enabled = true;
                GetComponent<CapsuleCollider>().enabled = false;
            }
            else
            {
                GetComponent<BoxCollider>().enabled = false;
                GetComponent<CapsuleCollider>().enabled = true;

            }
        }

        private void AttachWorldAnchor()
        {            
            StartCoroutine(WaitToAddAnchor());
        }

        IEnumerator WaitToAddAnchor()
        {
            print("Attaching World Anchor");
            if (anchorPoints)
            {
                if (hostRigidbody != null)
                {
                    hostRigidbody.isKinematic = true;
                }
                    Destroy(hostRigidbody);
            }

            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();

            if (WorldAnchorManager.Instance != null && anchorPoints)
            {


                // Add world anchor when object placement is done.
                WorldAnchorManager.Instance.AttachAnchor(gameObject);

            }

            yield return new WaitForEndOfFrame();

            if (anchor == null)
                anchor = GetComponent<WorldAnchor>();
            if (anchor != null)
            {
                print("Anchor Component found");
                anchor.OnTrackingChanged += UpdateShapes;
            }
            anchorAttached = true;

            if(onStartDisable)
            {
                StartCoroutine(DisableAfterSpawn());
            }
        }


        private void RemoveWorldAnchor()
        {
            StartCoroutine(WaitToSetupRigidbody());
        }

        public void UpdateShapes(WorldAnchor self, bool located)
        {
            StartCoroutine(WaitToUpdateShapes());
        }

        IEnumerator WaitToUpdateShapes()
        {
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            Interact interact = GetComponent<Interact>();
            if (interact && PolygonManager.Instance)
            {
                if (interact.interactObj == Interact.InteractObj.Point)
                {
                    PolygonManager.Instance.UpdateAllPointPositions();
                    PolygonManager.Instance.UpdateLineLength();
                    PolygonManager.Instance.UpdatePolygonMesh();
                }
            }
        }

        IEnumerator WaitToSetupRigidbody()
        {
            if (anchor == null)
                anchor = GetComponent<WorldAnchor>();
            if (anchor != null)
            {
                print("Anchor Component found");
                anchor.OnTrackingChanged -= UpdateShapes;
            }

            if (WorldAnchorManager.Instance != null && anchorPoints)
            {
                //Removes existing world anchor if any exist.
                WorldAnchorManager.Instance.RemoveAnchor(gameObject);
            }

            

            print("setting up rigidbody");
            bool inEditor = false;
#if UNITY_EDITOR
            inEditor = true;
#endif
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            if (anchorPoints)
            {
                if (!inEditor)
                {
                    //yield return new WaitUntil(() => WorldAnchorManager.Instance.deletedSuccessfully == true);
                }
                SetupRigidbody();
            }
                

            WorldAnchorManager.Instance.deletedSuccessfully = false;
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            
        }

        public IEnumerator DisableAfterSpawn()
        {
            yield return new WaitUntil(() => anchorAttached == true);
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            onStartDisable = false;
            this.enabled = false;
        }

        private void SetupRigidbody()
        {
            if (hostRigidbody == null)
            {
                HostTransform.gameObject.AddComponent<Rigidbody>();
                hostRigidbody = HostTransform.GetComponent<Rigidbody>();

                hostRigidbody.mass = 1;
                hostRigidbody.drag = 0;
                hostRigidbody.angularDrag = .05f;
                hostRigidbody.useGravity = false;
                hostRigidbody.isKinematic = true;
                hostRigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                hostRigidbody.constraints = RigidbodyConstraints.FreezeRotation;
            }
            
        }
    }
}
