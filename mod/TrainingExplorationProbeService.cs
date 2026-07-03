using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace AI_Train;

internal sealed class TrainingExplorationProbeService : IDisposable
{
    private const string StructureSpawnerTypeName = "Il2CppRUMBLE.MoveSystem.StructureSpawner";
    private const string StructureTypeName = "Il2CppRUMBLE.MoveSystem.Structure";
    private const string PlayerMovementTypeName = "Il2CppRUMBLE.Players.Subsystems.PlayerMovement";
    private const float SummonObservationDelaySeconds = 1.25f;
    private const float MoveObservationDelaySeconds = 0.4f;
    private const float DummyObservationDelaySeconds = 0.2f;
    private const float InteractionObservationDelaySeconds = 1.5f;
    private const int MaxReportedObjects = 32;

    private readonly Func<GameObject> _resolvePrimaryActor;
    private readonly Func<string, object, string> _writeJson;
    private readonly Action<string, string, string> _recordProbeStatus;
    private readonly Action<string> _logInfo;
    private readonly Action<string> _logWarn;
    private readonly Action<string> _logError;
    private ActiveSummonProbe _activeSummonProbe;
    private ActiveMoveProbe _activeMoveProbe;
    private ActiveDummyProbe _activeDummyProbe;
    private ActiveInteractionProbe _activeInteractionProbe;

    public TrainingExplorationProbeService(
        Func<GameObject> resolvePrimaryActor,
        Func<string, object, string> writeJson,
        Action<string, string, string> recordProbeStatus,
        Action<string> logInfo,
        Action<string> logWarn,
        Action<string> logError)
    {
        _resolvePrimaryActor = resolvePrimaryActor ?? (() => null);
        _writeJson = writeJson ?? ((_, _) => null);
        _recordProbeStatus = recordProbeStatus ?? ((_, _, _) => { });
        _logInfo = logInfo ?? (_ => { });
        _logWarn = logWarn ?? (_ => { });
        _logError = logError ?? (_ => { });
    }

    public bool IsBusy =>
        _activeSummonProbe != null ||
        _activeMoveProbe != null ||
        _activeDummyProbe != null ||
        _activeInteractionProbe != null;

    public TrainingBridgeBootstrapActionResult StartSingleActorSummonProbe(string reason)
    {
        if (IsBusy)
        {
            return CreateBusyResult();
        }

        var timestampUtc = DateTime.UtcNow;
        var actor = _resolvePrimaryActor();
        var actorPath = SafePath(actor);
        var report = new TrainingActiveProbeReport
        {
            timestampUtc = timestampUtc,
            reason = reason,
            probeName = "single_actor_summon_probe",
            status = "running",
            passiveOnly = false,
            primaryActorPath = actorPath,
            candidatePolicy = new List<string>
            {
                $"exact component type: {StructureSpawnerTypeName}",
                "exact instance method: Spawn()",
                "component must be active in a loaded scene",
                "configured structure prefab getter must return a non-null object",
                "one candidate and one invocation per request"
            },
            requiredEvidence = new List<string>
            {
                "a new or newly activated loaded-scene object",
                $"the observed object exposes {StructureTypeName} or matches the configured prefab name",
                "actor ownership is confirmed only when the spawner is under the selected actor"
            }
        };

        if (actor == null)
        {
            report.status = "failed";
            report.failureReason = "primary_actor_missing";
            report.conclusion = "No gameplay method was invoked because the primary actor could not be resolved.";
            return WriteImmediateProbeReport(report, "single_actor_summon_probe", "latest_single_actor_summon_probe.json", "summon");
        }

        var candidateSearch = FindSummonCandidates(actor);
        report.candidates = candidateSearch.Records;
        var candidate = candidateSearch.Eligible.FirstOrDefault();
        if (candidate == null)
        {
            report.status = "no_safe_candidate";
            report.failureReason = candidateSearch.Records.Count == 0
                ? "structure_spawner_component_not_found"
                : "no_active_configured_structure_spawner";
            report.conclusion = "No gameplay method was invoked because no candidate satisfied the exact summon policy.";
            return WriteImmediateProbeReport(report, "single_actor_summon_probe", "latest_single_actor_summon_probe.json", "summon");
        }

        report.selectedCandidate = candidate.Record;
        var before = CaptureSceneObjects();
        report.before = before.Summary;
        report.attempts.Add(new TrainingProbeAttempt
        {
            attemptedAtUtc = DateTime.UtcNow,
            componentPath = candidate.Record.componentPath,
            typeFullName = candidate.Record.typeFullName,
            memberName = candidate.Record.memberName,
            parameterSummary = candidate.Record.parameterSummary,
            riskLevel = "explicitly-enabled",
            outcome = "invocation_pending"
        });

        var fileName = $"single_actor_summon_probe_{timestampUtc:yyyyMMdd_HHmmss_fff}.json";
        var reportPath = WriteProbeReport(fileName, "latest_single_actor_summon_probe.json", report);
        _recordProbeStatus("summon", "running", reportPath);
        _logInfo(
            $"Single-actor summon probe starting ({reason}). candidate={candidate.Record.typeFullName}.{candidate.Record.memberName} " +
            $"path={candidate.Record.componentPath} actorBound={candidate.Record.actorBound}.");

        try
        {
            candidate.Method.Invoke(candidate.Component, Array.Empty<object>());
            report.attempts[0].invoked = true;
            report.attempts[0].outcome = "invoked_waiting_for_evidence";
            WriteProbeReport(fileName, "latest_single_actor_summon_probe.json", report);
            _activeSummonProbe = new ActiveSummonProbe
            {
                FileName = fileName,
                ReportPath = reportPath,
                Report = report,
                Before = before,
                ConfiguredPrefabName = candidate.Record.configuredObjectName,
                CompleteAtUnscaledTime = Time.unscaledTime + SummonObservationDelaySeconds
            };
            return new TrainingBridgeBootstrapActionResult
            {
                Succeeded = true,
                Status = "running",
                ReportPath = reportPath,
                Message = "One exact StructureSpawner.Spawn() candidate was invoked; evidence collection is in progress."
            };
        }
        catch (Exception ex)
        {
            var message = UnwrapInvocationException(ex);
            report.status = "failed";
            report.failureReason = "candidate_invocation_threw";
            report.conclusion = $"The selected candidate threw before observation: {message}";
            report.attempts[0].outcome = "threw";
            report.attempts[0].error = message;
            report.completedAtUtc = DateTime.UtcNow;
            reportPath = WriteProbeReport(fileName, "latest_single_actor_summon_probe.json", report);
            _recordProbeStatus("summon", report.status, reportPath);
            _logError($"Single-actor summon probe invocation failed: {message}");
            return new TrainingBridgeBootstrapActionResult
            {
                Succeeded = false,
                Status = report.status,
                ErrorCode = report.failureReason,
                ReportPath = reportPath,
                Message = report.conclusion
            };
        }
    }

    public TrainingBridgeBootstrapActionResult StartMoveProbe(string reason)
    {
        if (IsBusy)
        {
            return CreateBusyResult();
        }

        var timestampUtc = DateTime.UtcNow;
        var actor = _resolvePrimaryActor();
        var report = new TrainingActiveProbeReport
        {
            timestampUtc = timestampUtc,
            reason = reason,
            probeName = "move_probe",
            status = "running",
            passiveOnly = false,
            primaryActorPath = SafePath(actor),
            candidatePolicy = new List<string>
            {
                $"exact component type: {PlayerMovementTypeName}",
                "exact instance method: Move(UnityEngine.Vector2)",
                "one small input sample followed by zero input",
                "cleanup may use exact Reposition(UnityEngine.Vector3, UnityEngine.Quaternion)"
            },
            requiredEvidence = new List<string>
            {
                "selected actor position before and after the input sample",
                "observed displacement greater than 0.01 world units",
                "zero input and cleanup outcome recorded"
            }
        };

        if (actor == null)
        {
            report.status = "failed";
            report.failureReason = "primary_actor_missing";
            report.conclusion = "No move method was invoked because the primary actor could not be resolved.";
            return WriteImmediateProbeReport(report, "move_probe", "latest_move_probe.json", "move");
        }

        report.modifierCandidates = FindModifierCandidates();
        report.modifierStatus = report.modifierCandidates.Count > 0 ? "unsafe_not_invoked" : "not_found";
        report.modifierConclusion = report.modifierCandidates.Count > 0
            ? "Modifier Execute methods require live IProcessor and StackConfiguration context. They were recorded but not invoked."
            : "No loaded-scene modifier component with an Execute method was found.";

        var candidates = FindMoveCandidates(actor);
        report.candidates = candidates.Select(candidate => candidate.Record).ToList();
        var selected = candidates.FirstOrDefault();
        if (selected == null)
        {
            report.status = "no_safe_candidate";
            report.failureReason = "player_movement_move_vector2_not_found";
            report.conclusion = "No gameplay method was invoked because the actor did not expose the exact bounded move candidate.";
            return WriteImmediateProbeReport(report, "move_probe", "latest_move_probe.json", "move");
        }

        report.selectedCandidate = selected.Record;
        report.beforeActor = CaptureActorState(actor);
        report.attempts.Add(new TrainingProbeAttempt
        {
            attemptedAtUtc = DateTime.UtcNow,
            componentPath = selected.Record.componentPath,
            typeFullName = selected.Record.typeFullName,
            memberName = selected.Record.memberName,
            parameterSummary = selected.Record.parameterSummary,
            riskLevel = "explicitly-enabled",
            outcome = "invocation_pending"
        });

        var fileName = $"move_probe_{timestampUtc:yyyyMMdd_HHmmss_fff}.json";
        var reportPath = WriteProbeReport(fileName, "latest_move_probe.json", report);
        _recordProbeStatus("move", "running", reportPath);
        _logInfo($"Move probe starting ({reason}). candidate={selected.Record.typeFullName}.Move(Vector2) actor={report.primaryActorPath}.");

        try
        {
            selected.MoveMethod.Invoke(selected.Component, new object[] { new Vector2(0f, 0.2f) });
            report.attempts[0].invoked = true;
            report.attempts[0].outcome = "input_applied_waiting_for_evidence";
            WriteProbeReport(fileName, "latest_move_probe.json", report);
            _activeMoveProbe = new ActiveMoveProbe
            {
                FileName = fileName,
                ReportPath = reportPath,
                Report = report,
                Actor = actor,
                Component = selected.Component,
                MoveMethod = selected.MoveMethod,
                RepositionMethod = selected.RepositionMethod,
                OriginalPosition = actor.transform.position,
                OriginalRotation = actor.transform.rotation,
                CompleteAtUnscaledTime = Time.unscaledTime + MoveObservationDelaySeconds
            };
            return new TrainingBridgeBootstrapActionResult
            {
                Succeeded = true,
                Status = "running",
                ReportPath = reportPath,
                Message = "One exact PlayerMovement.Move(Vector2) input sample was applied; displacement observation is in progress."
            };
        }
        catch (Exception ex)
        {
            var message = UnwrapInvocationException(ex);
            report.status = "failed";
            report.failureReason = "move_invocation_threw";
            report.conclusion = $"The selected move candidate threw: {message}";
            report.attempts[0].outcome = "threw";
            report.attempts[0].error = message;
            report.completedAtUtc = DateTime.UtcNow;
            reportPath = WriteProbeReport(fileName, "latest_move_probe.json", report);
            _recordProbeStatus("move", report.status, reportPath);
            _logError($"Move probe invocation failed: {message}");
            return new TrainingBridgeBootstrapActionResult
            {
                Succeeded = false,
                Status = report.status,
                ErrorCode = report.failureReason,
                ReportPath = reportPath,
                Message = report.conclusion
            };
        }
    }

    public TrainingBridgeBootstrapActionResult StartMultiActorProbe(string reason)
    {
        if (IsBusy)
        {
            return CreateBusyResult();
        }

        var timestampUtc = DateTime.UtcNow;
        var actor = _resolvePrimaryActor();
        var report = new TrainingActiveProbeReport
        {
            timestampUtc = timestampUtc,
            reason = reason,
            probeName = "multi_actor_probe",
            status = "running",
            passiveOnly = false,
            primaryActorPath = SafePath(actor),
            fullSecondActor = false,
            dummyTarget = false,
            cloneAttempted = false,
            cloneSkipReason =
                "Instantiating an active player root runs Awake/OnEnable before duplicate camera, input, and networking components can be neutralized. No live-safe actor prefab or spawning manager is confirmed.",
            candidatePolicy = new List<string>
            {
                "do not clone an active player root without a confirmed lifecycle-safe path",
                "fall back to one mod-owned kinematic dummy target",
                "move only the dummy and verify the selected actor remains independent",
                "destroy the dummy after the observation window"
            },
            requiredEvidence = new List<string>
            {
                "dummy target exists in a loaded scene",
                "dummy target moves at least 0.4 world units",
                "primary actor moves less than 0.05 world units during the same sample",
                "dummy cleanup is scheduled"
            }
        };

        if (actor == null)
        {
            report.status = "failed";
            report.failureReason = "primary_actor_missing";
            report.conclusion = "No target was created because the primary actor could not be resolved.";
            return WriteImmediateProbeReport(report, "multi_actor_probe", "latest_multi_actor_probe.json", "multiActor");
        }

        GameObject target = null;
        try
        {
            var forward = Vector3.ProjectOnPlane(actor.transform.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.25f)
            {
                forward = Vector3.forward;
            }

            var right = Vector3.ProjectOnPlane(actor.transform.right, Vector3.up).normalized;
            if (right.sqrMagnitude < 0.25f)
            {
                right = Vector3.right;
            }

            target = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            target.name = $"AI_Train_DummyTarget_{timestampUtc:HHmmssfff}";
            target.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
            target.transform.position = actor.transform.position + (forward * 3f) + (Vector3.up * 0.9f);
            target.transform.rotation = Quaternion.LookRotation(-forward, Vector3.up);
            target.transform.localScale = new Vector3(0.65f, 1f, 0.65f);
            var body = target.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            report.beforeActor = CaptureActorState(actor);
            report.targetBefore = CaptureObjectState(target);
            report.selectedCandidate = new TrainingProbeCandidate
            {
                componentPath = SafePath(target),
                sceneName = SafeSceneName(target),
                typeFullName = "AI_Train.DummyTarget",
                memberName = "Transform.position",
                memberKind = "property",
                parameterSummary = "safe offset plus 0.5 world-unit lateral sample",
                actorBound = false,
                activeInHierarchy = target.activeInHierarchy,
                configuredObjectName = target.name,
                eligible = true
            };
            report.attempts.Add(new TrainingProbeAttempt
            {
                attemptedAtUtc = DateTime.UtcNow,
                componentPath = SafePath(target),
                typeFullName = "AI_Train.DummyTarget",
                memberName = "Transform.position",
                parameterSummary = "lateral delta 0.5",
                riskLevel = "mod-owned-object-only",
                invoked = true,
                outcome = "dummy_created_and_moved"
            });

            var originalTargetPosition = target.transform.position;
            target.transform.position = originalTargetPosition + (right * 0.5f);
            var fileName = $"multi_actor_probe_{timestampUtc:yyyyMMdd_HHmmss_fff}.json";
            var reportPath = WriteProbeReport(fileName, "latest_multi_actor_probe.json", report);
            _recordProbeStatus("multiActor", "running", reportPath);
            _activeDummyProbe = new ActiveDummyProbe
            {
                FileName = fileName,
                ReportPath = reportPath,
                Report = report,
                Actor = actor,
                ActorStartPosition = actor.transform.position,
                Target = target,
                TargetStartPosition = originalTargetPosition,
                CompleteAtUnscaledTime = Time.unscaledTime + DummyObservationDelaySeconds
            };
            _logInfo($"Multi-actor feasibility probe started ({reason}) using dummy target {target.name}; actor cloning was not attempted.");
            return new TrainingBridgeBootstrapActionResult
            {
                Succeeded = true,
                Status = "running",
                ReportPath = reportPath,
                Message = "A mod-owned dummy target was created for an independence check; full actor cloning was intentionally not attempted."
            };
        }
        catch (Exception ex)
        {
            if (target != null)
            {
                UnityObject.Destroy(target);
            }

            var message = UnwrapInvocationException(ex);
            report.status = "failed";
            report.failureReason = "dummy_target_setup_failed";
            report.conclusion = $"Dummy target setup failed: {message}";
            report.completedAtUtc = DateTime.UtcNow;
            var fileName = $"multi_actor_probe_{timestampUtc:yyyyMMdd_HHmmss_fff}.json";
            var path = WriteProbeReport(fileName, "latest_multi_actor_probe.json", report);
            _recordProbeStatus("multiActor", report.status, path);
            _logError($"Multi-actor feasibility probe setup failed: {message}");
            return new TrainingBridgeBootstrapActionResult
            {
                Succeeded = false,
                Status = report.status,
                ErrorCode = report.failureReason,
                ReportPath = path,
                Message = report.conclusion
            };
        }
    }

    public TrainingBridgeBootstrapActionResult StartActorInteractionProbe(string reason)
    {
        if (IsBusy)
        {
            return CreateBusyResult();
        }

        var timestampUtc = DateTime.UtcNow;
        var actor = _resolvePrimaryActor();
        var report = new TrainingActiveProbeReport
        {
            timestampUtc = timestampUtc,
            reason = reason,
            probeName = "actor_interaction_probe",
            status = "running",
            passiveOnly = false,
            primaryActorPath = SafePath(actor),
            damageEvidence = false,
            interactionEvidenceLevel = "none",
            candidatePolicy = new List<string>
            {
                "create one mod-owned kinematic dummy target",
                "create one mod-owned rigidbody projectile at a safe actor-relative offset",
                "collect OnCollisionEnter, OnTriggerEnter, and collider-overlap evidence for at most 1.5 seconds",
                "do not invoke damage, health, hit, RPC, or ownership methods",
                "destroy both probe objects after observation"
            },
            requiredEvidence = new List<string>
            {
                "a collision or trigger recorder identifies the paired probe object, or the paired collider bounds overlap",
                "the report identifies the exact contact evidence level",
                "damage remains unconfirmed unless a separate real state change is observed"
            }
        };

        if (actor == null)
        {
            report.status = "failed";
            report.failureReason = "primary_actor_missing";
            report.conclusion = "No interaction objects were created because the primary actor could not be resolved.";
            return WriteImmediateProbeReport(
                report,
                "actor_interaction_probe",
                "latest_actor_interaction_probe.json",
                "interaction");
        }

        GameObject target = null;
        GameObject projectile = null;
        try
        {
            var forward = Vector3.ProjectOnPlane(actor.transform.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.25f)
            {
                forward = Vector3.forward;
            }

            var origin = actor.transform.position + (Vector3.up * 1f);
            target = GameObject.CreatePrimitive(PrimitiveType.Cube);
            target.name = $"AI_Train_InteractionTarget_{timestampUtc:HHmmssfff}";
            target.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
            target.transform.position = origin + (forward * 3.5f);
            target.transform.localScale = new Vector3(1f, 1.5f, 1f);
            var targetCollider = target.GetComponent<Collider>();
            targetCollider.enabled = true;
            targetCollider.isTrigger = true;
            var targetBody = target.AddComponent<Rigidbody>();
            targetBody.isKinematic = true;
            targetBody.useGravity = false;
            targetBody.detectCollisions = true;
            var targetRecorder = target.AddComponent<TrainingProbeCollisionRecorder>();

            projectile = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            projectile.name = $"AI_Train_InteractionProjectile_{timestampUtc:HHmmssfff}";
            projectile.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
            projectile.transform.position = origin + (forward * 1.25f);
            projectile.transform.localScale = Vector3.one * 0.45f;
            var projectileCollider = projectile.GetComponent<Collider>();
            projectileCollider.enabled = true;
            projectileCollider.isTrigger = false;
            var projectileBody = projectile.AddComponent<Rigidbody>();
            projectileBody.useGravity = false;
            projectileBody.isKinematic = false;
            projectileBody.detectCollisions = true;
            projectileBody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            Physics.IgnoreCollision(projectileCollider, targetCollider, false);
            var projectileRecorder = projectile.AddComponent<TrainingProbeCollisionRecorder>();

            report.targetBefore = CaptureObjectState(target);
            report.projectileBefore = CaptureObjectState(projectile);
            report.selectedCandidate = new TrainingProbeCandidate
            {
                componentPath = SafePath(projectile),
                sceneName = SafeSceneName(projectile),
                typeFullName = "UnityEngine.Rigidbody",
                memberName = "velocity",
                memberKind = "property",
                parameterSummary = "actor-forward velocity 5 world units/second",
                actorBound = false,
                activeInHierarchy = projectile.activeInHierarchy,
                configuredObjectName = projectile.name,
                eligible = true
            };
            report.attempts.Add(new TrainingProbeAttempt
            {
                attemptedAtUtc = DateTime.UtcNow,
                componentPath = SafePath(projectile),
                typeFullName = "UnityEngine.Rigidbody",
                memberName = "velocity",
                parameterSummary = "forward * 5",
                riskLevel = "mod-owned-objects-only",
                invoked = true,
                outcome = "projectile_launched_waiting_for_collision"
            });

            projectileBody.velocity = forward * 5f;
            var fileName = $"actor_interaction_probe_{timestampUtc:yyyyMMdd_HHmmss_fff}.json";
            var reportPath = WriteProbeReport(fileName, "latest_actor_interaction_probe.json", report);
            _recordProbeStatus("interaction", "running", reportPath);
            _activeInteractionProbe = new ActiveInteractionProbe
            {
                FileName = fileName,
                ReportPath = reportPath,
                Report = report,
                Target = target,
                TargetCollider = targetCollider,
                TargetRecorder = targetRecorder,
                Projectile = projectile,
                ProjectileCollider = projectileCollider,
                ProjectileBody = projectileBody,
                ProjectileRecorder = projectileRecorder,
                CompleteAtUnscaledTime = Time.unscaledTime + InteractionObservationDelaySeconds
            };
            _logInfo(
                $"Actor interaction probe started ({reason}). projectile={projectile.name} target={target.name}; " +
                "damage methods are not being invoked.");
            return new TrainingBridgeBootstrapActionResult
            {
                Succeeded = true,
                Status = "running",
                ReportPath = reportPath,
                Message = "One mod-owned projectile was launched toward one mod-owned dummy target; collision observation is in progress."
            };
        }
        catch (Exception ex)
        {
            if (projectile != null)
            {
                UnityObject.Destroy(projectile);
            }
            if (target != null)
            {
                UnityObject.Destroy(target);
            }

            var message = UnwrapInvocationException(ex);
            report.status = "failed";
            report.failureReason = "interaction_setup_failed";
            report.conclusion = $"Interaction probe setup failed: {message}";
            report.cleanupNotes.Add("probe_object_destroy_scheduled_after_setup_error");
            report.completedAtUtc = DateTime.UtcNow;
            var fileName = $"actor_interaction_probe_{timestampUtc:yyyyMMdd_HHmmss_fff}.json";
            var path = WriteProbeReport(fileName, "latest_actor_interaction_probe.json", report);
            _recordProbeStatus("interaction", report.status, path);
            _logError($"Actor interaction probe setup failed: {message}");
            return new TrainingBridgeBootstrapActionResult
            {
                Succeeded = false,
                Status = report.status,
                ErrorCode = report.failureReason,
                ReportPath = path,
                Message = report.conclusion
            };
        }
    }

    public void Tick()
    {
        if (_activeSummonProbe != null && Time.unscaledTime >= _activeSummonProbe.CompleteAtUnscaledTime)
        {
            CompleteSummonProbe(_activeSummonProbe);
            _activeSummonProbe = null;
        }

        if (_activeMoveProbe != null && Time.unscaledTime >= _activeMoveProbe.CompleteAtUnscaledTime)
        {
            CompleteMoveProbe(_activeMoveProbe);
            _activeMoveProbe = null;
        }

        if (_activeDummyProbe != null && Time.unscaledTime >= _activeDummyProbe.CompleteAtUnscaledTime)
        {
            CompleteDummyProbe(_activeDummyProbe);
            _activeDummyProbe = null;
        }

        if (_activeInteractionProbe != null)
        {
            ObserveInteractionContact(_activeInteractionProbe);
            if (Time.unscaledTime >= _activeInteractionProbe.CompleteAtUnscaledTime)
            {
                CompleteInteractionProbe(_activeInteractionProbe);
                _activeInteractionProbe = null;
            }
        }
    }

    public void Dispose()
    {
        if (_activeSummonProbe != null)
        {
            _activeSummonProbe.Report.status = "aborted";
            _activeSummonProbe.Report.failureReason = "mod_disposed_during_probe";
            _activeSummonProbe.Report.conclusion = "The probe was aborted before its observation window completed.";
            _activeSummonProbe.Report.completedAtUtc = DateTime.UtcNow;
            var path = WriteProbeReport(
                _activeSummonProbe.FileName,
                "latest_single_actor_summon_probe.json",
                _activeSummonProbe.Report);
            _recordProbeStatus("summon", "aborted", path);
            _activeSummonProbe = null;
        }

        AbortMoveProbe();
        AbortDummyProbe();
        AbortInteractionProbe();
    }

    private void CompleteInteractionProbe(ActiveInteractionProbe active)
    {
        try
        {
            if (active.ProjectileBody != null)
            {
                active.ProjectileBody.velocity = Vector3.zero;
                active.ProjectileBody.angularVelocity = Vector3.zero;
            }

            active.Report.targetAfter = CaptureObjectState(active.Target);
            active.Report.projectileAfter = CaptureObjectState(active.Projectile);
            active.Report.collisionEnterCount =
                (active.TargetRecorder?.CollisionEnterCount ?? 0) +
                (active.ProjectileRecorder?.CollisionEnterCount ?? 0);
            active.Report.triggerEnterCount =
                (active.TargetRecorder?.TriggerEnterCount ?? 0) +
                (active.ProjectileRecorder?.TriggerEnterCount ?? 0);
            active.Report.colliderBoundsOverlapObserved = active.ColliderBoundsOverlapObserved;
            if (!string.IsNullOrWhiteSpace(active.TargetRecorder?.LastOtherName))
            {
                active.Report.collisionOtherNames.Add(active.TargetRecorder.LastOtherName);
            }
            if (!string.IsNullOrWhiteSpace(active.ProjectileRecorder?.LastOtherName))
            {
                active.Report.collisionOtherNames.Add(active.ProjectileRecorder.LastOtherName);
            }
            if (!string.IsNullOrWhiteSpace(active.TargetRecorder?.LastTriggerOtherName))
            {
                active.Report.contactOtherNames.Add(active.TargetRecorder.LastTriggerOtherName);
            }
            if (!string.IsNullOrWhiteSpace(active.ProjectileRecorder?.LastTriggerOtherName))
            {
                active.Report.contactOtherNames.Add(active.ProjectileRecorder.LastTriggerOtherName);
            }
            active.Report.collisionOtherNames = active.Report.collisionOtherNames
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            active.Report.contactOtherNames = active.Report.collisionOtherNames
                .Concat(active.Report.contactOtherNames)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var pairedCollisionObserved =
                (active.TargetRecorder?.CollisionEnterCount ?? 0) > 0 &&
                string.Equals(
                    active.TargetRecorder?.LastOtherName,
                    active.Projectile?.name,
                    StringComparison.Ordinal) ||
                (active.ProjectileRecorder?.CollisionEnterCount ?? 0) > 0 &&
                string.Equals(
                    active.ProjectileRecorder?.LastOtherName,
                    active.Target?.name,
                    StringComparison.Ordinal);
            var pairedTriggerObserved =
                (active.TargetRecorder?.TriggerEnterCount ?? 0) > 0 &&
                string.Equals(
                    active.TargetRecorder?.LastTriggerOtherName,
                    active.Projectile?.name,
                    StringComparison.Ordinal) ||
                (active.ProjectileRecorder?.TriggerEnterCount ?? 0) > 0 &&
                string.Equals(
                    active.ProjectileRecorder?.LastTriggerOtherName,
                    active.Target?.name,
                    StringComparison.Ordinal);

            if (pairedCollisionObserved)
            {
                active.Report.status = "collision_confirmed";
                active.Report.interactionEvidenceLevel = "unity_collision";
                active.Report.conclusion =
                    "Direct OnCollisionEnter evidence confirms contact between the mod-owned projectile and dummy target. Damage and game combat remain unconfirmed.";
                active.Report.attempts[0].outcome = "paired_collision_observed";
            }
            else if (pairedTriggerObserved)
            {
                active.Report.status = "contact_confirmed";
                active.Report.interactionEvidenceLevel = "unity_trigger";
                active.Report.conclusion =
                    "Direct OnTriggerEnter evidence confirms overlap between the mod-owned projectile and dummy target. Damage and game combat remain unconfirmed.";
                active.Report.attempts[0].outcome = "paired_trigger_observed";
            }
            else if (active.ColliderBoundsOverlapObserved)
            {
                active.Report.status = "contact_confirmed";
                active.Report.interactionEvidenceLevel = "collider_bounds_overlap";
                active.Report.conclusion =
                    "Per-frame collider bounds overlapped for the mod-owned projectile and dummy target. Unity callbacks, damage, and game combat remain unconfirmed.";
                active.Report.attempts[0].outcome = "paired_collider_bounds_overlap_observed";
            }
            else
            {
                active.Report.status = "failed";
                active.Report.failureReason = "paired_collision_not_observed";
                active.Report.interactionEvidenceLevel = "none";
                active.Report.conclusion =
                    $"No paired collision was observed during the bounded window. collisionEnterCount={active.Report.collisionEnterCount}.";
                active.Report.attempts[0].outcome = "no_paired_collision_observed";
            }

            CleanupInteractionObjects(active);
            active.Report.completedAtUtc = DateTime.UtcNow;
            var path = WriteProbeReport(
                active.FileName,
                "latest_actor_interaction_probe.json",
                active.Report);
            _recordProbeStatus("interaction", active.Report.status, path);
            _logInfo(
                $"Actor interaction probe completed. status={active.Report.status} " +
                $"collisionEnterCount={active.Report.collisionEnterCount} " +
                $"triggerEnterCount={active.Report.triggerEnterCount} " +
                $"boundsOverlap={active.Report.colliderBoundsOverlapObserved} " +
                $"damageEvidence={active.Report.damageEvidence}.");
        }
        catch (Exception ex)
        {
            CleanupInteractionObjects(active);
            var message = UnwrapInvocationException(ex);
            active.Report.status = "failed";
            active.Report.failureReason = "interaction_observation_failed";
            active.Report.conclusion = $"Interaction observation failed: {message}";
            active.Report.completedAtUtc = DateTime.UtcNow;
            var path = WriteProbeReport(
                active.FileName,
                "latest_actor_interaction_probe.json",
                active.Report);
            _recordProbeStatus("interaction", active.Report.status, path);
            _logError($"Actor interaction probe observation failed: {message}");
        }
    }

    private static void ObserveInteractionContact(ActiveInteractionProbe active)
    {
        if (active?.TargetCollider == null || active.ProjectileCollider == null)
        {
            return;
        }

        if (active.TargetCollider.enabled &&
            active.ProjectileCollider.enabled &&
            active.TargetCollider.bounds.Intersects(active.ProjectileCollider.bounds))
        {
            active.ColliderBoundsOverlapObserved = true;
        }
    }

    private static void CleanupInteractionObjects(ActiveInteractionProbe active)
    {
        if (active.Projectile != null)
        {
            UnityObject.Destroy(active.Projectile);
            active.Report.cleanupNotes.Add("interaction_projectile_destroy_scheduled");
        }
        else
        {
            active.Report.cleanupNotes.Add("interaction_projectile_already_gone");
        }

        if (active.Target != null)
        {
            UnityObject.Destroy(active.Target);
            active.Report.cleanupNotes.Add("interaction_target_destroy_scheduled");
        }
        else
        {
            active.Report.cleanupNotes.Add("interaction_target_already_gone");
        }
    }

    private void CompleteDummyProbe(ActiveDummyProbe active)
    {
        try
        {
            active.Report.afterActor = CaptureActorState(active.Actor);
            active.Report.targetAfter = CaptureObjectState(active.Target);
            active.Report.actorObservedDistance = active.Actor != null
                ? Vector3.Distance(active.ActorStartPosition, active.Actor.transform.position)
                : float.PositiveInfinity;
            active.Report.targetObservedDistance = active.Target != null
                ? Vector3.Distance(active.TargetStartPosition, active.Target.transform.position)
                : 0f;

            var targetIndependent = active.Target != null &&
                                    active.Target.activeInHierarchy &&
                                    active.Report.targetObservedDistance >= 0.4f &&
                                    active.Report.actorObservedDistance < 0.05f;
            if (targetIndependent)
            {
                active.Report.status = "dummy_target_confirmed";
                active.Report.dummyTarget = true;
                active.Report.conclusion =
                    "A mod-owned dummy target existed and moved independently while the primary actor remained stable. Full second-actor support is not confirmed.";
                active.Report.attempts[0].outcome = "dummy_independence_observed";
            }
            else
            {
                active.Report.status = "failed";
                active.Report.failureReason = "dummy_independence_not_observed";
                active.Report.conclusion =
                    $"Dummy independence failed. targetDistance={active.Report.targetObservedDistance:F4} actorDistance={active.Report.actorObservedDistance:F4}.";
                active.Report.attempts[0].outcome = "dummy_independence_not_observed";
            }

            if (active.Target != null)
            {
                UnityObject.Destroy(active.Target);
                active.Report.cleanupNotes.Add("dummy_target_destroy_scheduled");
            }
            else
            {
                active.Report.cleanupNotes.Add("dummy_target_already_gone");
            }

            active.Report.completedAtUtc = DateTime.UtcNow;
            var path = WriteProbeReport(active.FileName, "latest_multi_actor_probe.json", active.Report);
            _recordProbeStatus("multiActor", active.Report.status, path);
            _logInfo(
                $"Multi-actor feasibility probe completed. status={active.Report.status} " +
                $"targetDistance={active.Report.targetObservedDistance:F4} actorDistance={active.Report.actorObservedDistance:F4}.");
        }
        catch (Exception ex)
        {
            if (active.Target != null)
            {
                UnityObject.Destroy(active.Target);
            }

            var message = UnwrapInvocationException(ex);
            active.Report.status = "failed";
            active.Report.failureReason = "dummy_observation_failed";
            active.Report.conclusion = $"Dummy target observation failed: {message}";
            active.Report.cleanupNotes.Add("dummy_target_destroy_scheduled_after_error");
            active.Report.completedAtUtc = DateTime.UtcNow;
            var path = WriteProbeReport(active.FileName, "latest_multi_actor_probe.json", active.Report);
            _recordProbeStatus("multiActor", active.Report.status, path);
            _logError($"Multi-actor feasibility probe observation failed: {message}");
        }
    }

    private void CompleteMoveProbe(ActiveMoveProbe active)
    {
        try
        {
            var cleanupMessages = new List<string>();
            try
            {
                active.MoveMethod.Invoke(active.Component, new object[] { Vector2.zero });
                cleanupMessages.Add("zero_input_applied");
            }
            catch (Exception ex)
            {
                cleanupMessages.Add($"zero_input_failed:{UnwrapInvocationException(ex)}");
            }

            active.Report.afterActor = CaptureActorState(active.Actor);
            active.Report.observedDistance = Vector3.Distance(
                active.OriginalPosition,
                active.Actor != null ? active.Actor.transform.position : active.OriginalPosition);

            if (active.RepositionMethod != null && active.Component != null)
            {
                try
                {
                    active.RepositionMethod.Invoke(
                        active.Component,
                        new object[] { active.OriginalPosition, active.OriginalRotation });
                    cleanupMessages.Add("reposition_invoked");
                }
                catch (Exception ex)
                {
                    cleanupMessages.Add($"reposition_failed:{UnwrapInvocationException(ex)}");
                }
            }
            else
            {
                cleanupMessages.Add("reposition_candidate_missing");
            }

            active.Report.cleanupNotes = cleanupMessages;
            if (active.Report.observedDistance > 0.01f)
            {
                active.Report.status = "confirmed";
                active.Report.conclusion =
                    $"PlayerMovement.Move(Vector2) produced {active.Report.observedDistance:F4} world units of observed actor displacement.";
                active.Report.attempts[0].outcome = "movement_observed";
            }
            else
            {
                active.Report.status = "failed";
                active.Report.failureReason = "no_actor_displacement_observed";
                active.Report.conclusion =
                    "The move method returned without actor displacement above the 0.01 world-unit evidence threshold.";
                active.Report.attempts[0].outcome = "invoked_no_movement_observed";
            }

            active.Report.completedAtUtc = DateTime.UtcNow;
            var path = WriteProbeReport(active.FileName, "latest_move_probe.json", active.Report);
            _recordProbeStatus("move", active.Report.status, path);
            _logInfo($"Move probe completed. status={active.Report.status} distance={active.Report.observedDistance:F4}.");
        }
        catch (Exception ex)
        {
            var message = UnwrapInvocationException(ex);
            active.Report.status = "failed";
            active.Report.failureReason = "move_observation_failed";
            active.Report.conclusion = $"Move observation failed: {message}";
            active.Report.completedAtUtc = DateTime.UtcNow;
            var path = WriteProbeReport(active.FileName, "latest_move_probe.json", active.Report);
            _recordProbeStatus("move", active.Report.status, path);
            _logError($"Move probe observation failed: {message}");
        }
    }

    private void AbortMoveProbe()
    {
        if (_activeMoveProbe == null)
        {
            return;
        }

        try
        {
            _activeMoveProbe.MoveMethod?.Invoke(_activeMoveProbe.Component, new object[] { Vector2.zero });
        }
        catch
        {
            // Best effort input cleanup during shutdown.
        }

        _activeMoveProbe.Report.status = "aborted";
        _activeMoveProbe.Report.failureReason = "mod_disposed_during_probe";
        _activeMoveProbe.Report.conclusion = "The move probe was aborted before its observation window completed.";
        _activeMoveProbe.Report.completedAtUtc = DateTime.UtcNow;
        var path = WriteProbeReport(_activeMoveProbe.FileName, "latest_move_probe.json", _activeMoveProbe.Report);
        _recordProbeStatus("move", "aborted", path);
        _activeMoveProbe = null;
    }

    private void AbortDummyProbe()
    {
        if (_activeDummyProbe == null)
        {
            return;
        }

        if (_activeDummyProbe.Target != null)
        {
            UnityObject.Destroy(_activeDummyProbe.Target);
        }

        _activeDummyProbe.Report.status = "aborted";
        _activeDummyProbe.Report.failureReason = "mod_disposed_during_probe";
        _activeDummyProbe.Report.conclusion = "The dummy-target probe was aborted before its observation window completed.";
        _activeDummyProbe.Report.cleanupNotes.Add("dummy_target_destroy_scheduled_during_abort");
        _activeDummyProbe.Report.completedAtUtc = DateTime.UtcNow;
        var path = WriteProbeReport(
            _activeDummyProbe.FileName,
            "latest_multi_actor_probe.json",
            _activeDummyProbe.Report);
        _recordProbeStatus("multiActor", "aborted", path);
        _activeDummyProbe = null;
    }

    private void AbortInteractionProbe()
    {
        if (_activeInteractionProbe == null)
        {
            return;
        }

        CleanupInteractionObjects(_activeInteractionProbe);
        _activeInteractionProbe.Report.status = "aborted";
        _activeInteractionProbe.Report.failureReason = "mod_disposed_during_probe";
        _activeInteractionProbe.Report.conclusion =
            "The interaction probe was aborted before its collision observation window completed.";
        _activeInteractionProbe.Report.completedAtUtc = DateTime.UtcNow;
        var path = WriteProbeReport(
            _activeInteractionProbe.FileName,
            "latest_actor_interaction_probe.json",
            _activeInteractionProbe.Report);
        _recordProbeStatus("interaction", "aborted", path);
        _activeInteractionProbe = null;
    }

    private static List<MoveCandidateRuntime> FindMoveCandidates(GameObject actor)
    {
        var candidates = new List<MoveCandidateRuntime>();
        var movementType = FindLoadedType(PlayerMovementTypeName);
        if (movementType == null)
        {
            return candidates;
        }

        foreach (var runtimeObject in FindRuntimeObjects(movementType))
        {
            if (runtimeObject is not Component component ||
                component == null ||
                !IsDescendantOrSelf(component.transform, actor.transform))
            {
                continue;
            }

            var type = movementType;
            var typeName = type.FullName ?? type.Name;

            var moveMethod = type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(method =>
                    string.Equals(method.Name, "Move", StringComparison.Ordinal) &&
                    !method.IsGenericMethod &&
                    method.GetParameters().Length == 1 &&
                    string.Equals(
                        method.GetParameters()[0].ParameterType.FullName,
                        typeof(Vector2).FullName,
                        StringComparison.Ordinal));
            if (moveMethod == null)
            {
                continue;
            }

            var repositionMethod = type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(method =>
                {
                    if (!string.Equals(method.Name, "Reposition", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    var parameters = method.GetParameters();
                    return parameters.Length == 2 &&
                           string.Equals(parameters[0].ParameterType.FullName, typeof(Vector3).FullName, StringComparison.Ordinal) &&
                           string.Equals(parameters[1].ParameterType.FullName, typeof(Quaternion).FullName, StringComparison.Ordinal);
                });
            candidates.Add(new MoveCandidateRuntime
            {
                Component = component,
                MoveMethod = moveMethod,
                RepositionMethod = repositionMethod,
                Record = new TrainingProbeCandidate
                {
                    componentPath = SafePath(component.gameObject),
                    sceneName = SafeSceneName(component.gameObject),
                    typeFullName = typeName,
                    memberName = "Move",
                    memberKind = "method",
                    parameterSummary = "(UnityEngine.Vector2)",
                    actorBound = true,
                    activeInHierarchy = component.gameObject.activeInHierarchy,
                    eligible = component.gameObject.activeInHierarchy,
                    rejectionReason = component.gameObject.activeInHierarchy ? null : "component_not_active"
                }
            });
        }

        return candidates
            .OrderByDescending(candidate => candidate.Record.eligible)
            .ThenBy(candidate => candidate.Record.componentPath, StringComparer.OrdinalIgnoreCase)
            .Where(candidate => candidate.Record.eligible)
            .ToList();
    }

    private static List<TrainingProbeCandidate> FindModifierCandidates()
    {
        var candidates = new List<TrainingProbeCandidate>();
        foreach (var type in GetLoadedModifierTypes())
        {
            foreach (var runtimeObject in FindRuntimeObjects(type))
            {
                if (runtimeObject is not Component component ||
                    component == null ||
                    !IsLoadedSceneObject(component.gameObject))
                {
                    continue;
                }

                foreach (var method in type
                             .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                             .Where(method => string.Equals(method.Name, "Execute", StringComparison.Ordinal))
                             .OrderBy(method => method.GetParameters().Length))
                {
                    candidates.Add(new TrainingProbeCandidate
                    {
                        componentPath = SafePath(component.gameObject),
                        sceneName = SafeSceneName(component.gameObject),
                        typeFullName = type.FullName ?? type.Name,
                        memberName = method.Name,
                        memberKind = "method",
                        parameterSummary = FormatParameterSummary(method),
                        riskLevel = "risky",
                        actorBound = false,
                        activeInHierarchy = component.gameObject.activeInHierarchy,
                        eligible = false,
                        rejectionReason = "requires_live_processor_and_stack_configuration_context"
                    });
                }
            }
        }

        return candidates
            .GroupBy(
                candidate => $"{candidate.componentPath}|{candidate.typeFullName}|{candidate.memberName}|{candidate.parameterSummary}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.typeFullName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.componentPath, StringComparer.OrdinalIgnoreCase)
            .Take(48)
            .ToList();
    }

    private static bool IsMoveSystemModifier(Type type)
    {
        var current = type;
        while (current != null)
        {
            var fullName = current.FullName ?? current.Name;
            if (string.Equals(fullName, "Il2CppRUMBLE.MoveSystem.Modifier", StringComparison.Ordinal) ||
                fullName.StartsWith("Il2CppRUMBLE.MoveSystem.", StringComparison.Ordinal) &&
                fullName.EndsWith("Modifier", StringComparison.Ordinal))
            {
                return true;
            }

            current = current.BaseType;
        }

        return false;
    }

    private static IEnumerable<Type> GetLoadedModifierTypes()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
            }
            catch
            {
                continue;
            }

            if (types == null)
            {
                continue;
            }

            foreach (var type in types)
            {
                if (type != null && IsMoveSystemModifier(type))
                {
                    yield return type;
                }
            }
        }
    }

    private static Type FindLoadedType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type;
            try
            {
                type = assembly.GetType(fullName, false, false);
            }
            catch
            {
                continue;
            }

            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    private static IEnumerable<object> FindRuntimeObjects(Type type)
    {
        if (type == null)
        {
            yield break;
        }

        var method = typeof(Resources).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(candidate =>
                candidate.Name == "FindObjectsOfTypeAll" &&
                candidate.IsGenericMethodDefinition &&
                candidate.GetGenericArguments().Length == 1 &&
                candidate.GetParameters().Length == 0);
        if (method == null)
        {
            yield break;
        }

        System.Collections.IEnumerable results;
        try
        {
            results = method.MakeGenericMethod(type)
                .Invoke(null, Array.Empty<object>()) as System.Collections.IEnumerable;
        }
        catch
        {
            yield break;
        }

        if (results == null)
        {
            yield break;
        }

        foreach (var result in results)
        {
            if (result != null)
            {
                yield return result;
            }
        }
    }

    private static string FormatParameterSummary(MethodBase method)
    {
        try
        {
            return $"({string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name))})";
        }
        catch
        {
            return "(unavailable)";
        }
    }

    private static TrainingProbeActorState CaptureActorState(GameObject actor)
    {
        if (actor == null)
        {
            return null;
        }

        return new TrainingProbeActorState
        {
            path = SafePath(actor),
            sceneName = SafeSceneName(actor),
            activeInHierarchy = actor.activeInHierarchy,
            position = TrainingProbeVector3.From(actor.transform.position),
            rotationEuler = TrainingProbeVector3.From(actor.transform.rotation.eulerAngles),
            frameCount = Time.frameCount,
            unscaledTime = Time.unscaledTime
        };
    }

    private static TrainingProbeObjectState CaptureObjectState(GameObject gameObject)
    {
        if (gameObject == null)
        {
            return null;
        }

        return new TrainingProbeObjectState
        {
            instanceId = gameObject.GetInstanceID(),
            name = gameObject.name,
            path = SafePath(gameObject),
            sceneName = SafeSceneName(gameObject),
            activeSelf = gameObject.activeSelf,
            activeInHierarchy = gameObject.activeInHierarchy,
            position = TrainingProbeVector3.From(gameObject.transform.position)
        };
    }

    private void CompleteSummonProbe(ActiveSummonProbe active)
    {
        try
        {
            var after = CaptureSceneObjects();
            var observed = FindObservedSummonObjects(active.Before, after, active.ConfiguredPrefabName);
            active.Report.after = after.Summary;
            active.Report.observedObjects = observed
                .Take(MaxReportedObjects)
                .Select(item => item.Evidence)
                .ToList();
            active.Report.attempts[0].observedObjectCount = observed.Count;

            foreach (var item in observed)
            {
                active.Report.cleanup.Add(CleanupObservedObject(item));
            }

            var actorBound = active.Report.selectedCandidate?.actorBound == true;
            if (observed.Count == 0)
            {
                active.Report.status = "failed";
                active.Report.failureReason = "no_new_object_observed";
                active.Report.conclusion = "The candidate returned without an observed new or newly activated structure-like object.";
                active.Report.attempts[0].outcome = "invoked_no_object_observed";
            }
            else if (actorBound)
            {
                active.Report.status = "confirmed";
                active.Report.conclusion = "An actor-bound StructureSpawner invocation produced directly observed structure-like object evidence.";
                active.Report.attempts[0].outcome = "object_observed";
            }
            else
            {
                active.Report.status = "partial";
                active.Report.failureReason = "actor_ownership_unverified";
                active.Report.conclusion = "A configured scene StructureSpawner produced object evidence, but the spawner was not under the selected actor.";
                active.Report.attempts[0].outcome = "object_observed_actor_link_unverified";
            }

            active.Report.completedAtUtc = DateTime.UtcNow;
            var path = WriteProbeReport(active.FileName, "latest_single_actor_summon_probe.json", active.Report);
            _recordProbeStatus("summon", active.Report.status, path);
            _logInfo(
                $"Single-actor summon probe completed. status={active.Report.status} observed={observed.Count} " +
                $"cleanupAttempts={active.Report.cleanup.Count}.");
        }
        catch (Exception ex)
        {
            var message = UnwrapInvocationException(ex);
            active.Report.status = "failed";
            active.Report.failureReason = "observation_failed";
            active.Report.conclusion = $"The post-invocation observation failed: {message}";
            active.Report.completedAtUtc = DateTime.UtcNow;
            var path = WriteProbeReport(active.FileName, "latest_single_actor_summon_probe.json", active.Report);
            _recordProbeStatus("summon", active.Report.status, path);
            _logError($"Single-actor summon probe observation failed: {message}");
        }
    }

    private SummonCandidateSearch FindSummonCandidates(GameObject actor)
    {
        var result = new SummonCandidateSearch();
        var spawnerType = FindLoadedType(StructureSpawnerTypeName);
        if (spawnerType == null)
        {
            _logWarn($"Summon candidate type not loaded: {StructureSpawnerTypeName}");
            return result;
        }

        foreach (var runtimeObject in FindRuntimeObjects(spawnerType))
        {
            if (runtimeObject is not Component component || component == null)
            {
                continue;
            }

            var type = spawnerType;
            var typeName = type.FullName ?? type.Name;

            var path = SafePath(component.gameObject);
            var record = new TrainingProbeCandidate
            {
                componentPath = path,
                sceneName = SafeSceneName(component.gameObject),
                typeFullName = typeName,
                memberName = "Spawn",
                memberKind = "method",
                parameterSummary = "()",
                actorBound = IsDescendantOrSelf(component.transform, actor.transform),
                activeInHierarchy = component.gameObject.activeInHierarchy
            };
            result.Records.Add(record);

            if (!IsLoadedSceneObject(component.gameObject))
            {
                record.rejectionReason = "component_not_in_loaded_scene";
                continue;
            }

            if (!component.gameObject.activeInHierarchy ||
                (component is Behaviour behaviour && !behaviour.enabled))
            {
                record.rejectionReason = "component_not_active";
                continue;
            }

            var method = type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(candidate => string.Equals(candidate.Name, "Spawn", StringComparison.Ordinal))
                .Where(candidate => !candidate.IsGenericMethod && candidate.GetParameters().Length == 0)
                .OrderBy(candidate => candidate.IsPublic ? 0 : 1)
                .FirstOrDefault();
            if (method == null)
            {
                record.rejectionReason = "exact_zero_argument_spawn_method_missing";
                continue;
            }

            var configuredObject = ReadConfiguredStructure(component, type);
            if (configuredObject == null)
            {
                record.rejectionReason = "configured_structure_prefab_missing";
                continue;
            }

            record.configuredObjectName = SafeUnityObjectName(configuredObject);
            record.eligible = true;
            result.Eligible.Add(new SummonCandidateRuntime
            {
                Component = component,
                Method = method,
                Record = record
            });
        }

        result.Records = result.Records
            .OrderByDescending(record => record.actorBound)
            .ThenByDescending(record => record.eligible)
            .ThenBy(record => record.componentPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.Eligible = result.Eligible
            .OrderByDescending(candidate => candidate.Record.actorBound)
            .ThenBy(candidate => candidate.Record.componentPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return result;
    }

    private static object ReadConfiguredStructure(Component component, Type type)
    {
        var property = type.GetProperty(
            "structureToSpawn",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (property?.GetIndexParameters().Length == 0)
        {
            try
            {
                return property.GetValue(component);
            }
            catch
            {
                return null;
            }
        }

        var getter = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method =>
                string.Equals(method.Name, "get_structureToSpawn", StringComparison.Ordinal) &&
                method.GetParameters().Length == 0);
        if (getter == null)
        {
            return null;
        }

        try
        {
            return getter.Invoke(component, Array.Empty<object>());
        }
        catch
        {
            return null;
        }
    }

    private static SceneObjectSnapshot CaptureSceneObjects()
    {
        var snapshot = new SceneObjectSnapshot();
        GameObject[] gameObjects;
        try
        {
            gameObjects = Resources.FindObjectsOfTypeAll<GameObject>();
        }
        catch
        {
            return snapshot;
        }

        foreach (var gameObject in gameObjects)
        {
            if (!IsLoadedSceneObject(gameObject))
            {
                continue;
            }

            var id = gameObject.GetInstanceID();
            var state = new TrainingProbeObjectState
            {
                instanceId = id,
                name = gameObject.name,
                path = SafePath(gameObject),
                sceneName = SafeSceneName(gameObject),
                activeSelf = gameObject.activeSelf,
                activeInHierarchy = gameObject.activeInHierarchy,
                position = TrainingProbeVector3.From(gameObject.transform.position)
            };
            snapshot.States[id] = state;
            snapshot.Objects[id] = gameObject;
        }

        snapshot.Summary = new TrainingProbeSnapshotSummary
        {
            objectCount = snapshot.States.Count,
            activeObjectCount = snapshot.States.Values.Count(state => state.activeInHierarchy)
        };
        return snapshot;
    }

    private static List<ObservedProbeObject> FindObservedSummonObjects(
        SceneObjectSnapshot before,
        SceneObjectSnapshot after,
        string configuredPrefabName)
    {
        var observed = new List<ObservedProbeObject>();
        foreach (var entry in after.States)
        {
            var state = entry.Value;
            var change = "none";
            if (!before.States.TryGetValue(entry.Key, out var oldState))
            {
                change = "new_instance";
            }
            else if (!oldState.activeInHierarchy && state.activeInHierarchy)
            {
                change = "activated_from_pool";
            }
            else
            {
                continue;
            }

            if (!after.Objects.TryGetValue(entry.Key, out var gameObject) || gameObject == null)
            {
                continue;
            }

            var componentTypes = SafeComponentTypeNames(gameObject);
            var structureComponent = componentTypes.Any(typeName =>
                string.Equals(typeName, StructureTypeName, StringComparison.Ordinal) ||
                typeName.EndsWith(".Structure", StringComparison.Ordinal));
            var prefabNameMatch = !string.IsNullOrWhiteSpace(configuredPrefabName) &&
                                  state.name.IndexOf(configuredPrefabName, StringComparison.OrdinalIgnoreCase) >= 0;
            if (!structureComponent && !prefabNameMatch)
            {
                continue;
            }

            observed.Add(new ObservedProbeObject
            {
                GameObject = gameObject,
                IsNewInstance = change == "new_instance",
                Evidence = new TrainingProbeObservedObject
                {
                    instanceId = state.instanceId,
                    name = state.name,
                    path = state.path,
                    sceneName = state.sceneName,
                    change = change,
                    activeInHierarchy = state.activeInHierarchy,
                    position = state.position,
                    componentTypes = componentTypes,
                    structureComponentObserved = structureComponent,
                    configuredPrefabNameMatched = prefabNameMatch
                }
            });
        }

        return observed
            .OrderByDescending(item => item.Evidence.structureComponentObserved)
            .ThenBy(item => item.Evidence.path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static TrainingProbeCleanupResult CleanupObservedObject(ObservedProbeObject observed)
    {
        var result = new TrainingProbeCleanupResult
        {
            instanceId = observed.Evidence.instanceId,
            objectPath = observed.Evidence.path
        };
        var gameObject = observed.GameObject;
        if (gameObject == null)
        {
            result.status = "already_gone";
            return result;
        }

        try
        {
            foreach (var component in gameObject.GetComponents<Component>())
            {
                if (component == null)
                {
                    continue;
                }

                var returnToPool = component.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(method =>
                        string.Equals(method.Name, "ReturnToPool", StringComparison.Ordinal) &&
                        method.GetParameters().Length == 0);
                if (returnToPool == null)
                {
                    continue;
                }

                returnToPool.Invoke(component, Array.Empty<object>());
                result.status = "returned_to_pool";
                result.cleanupMethod = $"{component.GetType().FullName}.ReturnToPool()";
                return result;
            }

            if (observed.IsNewInstance)
            {
                UnityObject.Destroy(gameObject);
                result.status = "destroy_scheduled";
                result.cleanupMethod = "UnityEngine.Object.Destroy";
                return result;
            }

            result.status = "not_cleaned";
            result.error = "Observed object was activated from a pool, but no ReturnToPool() method was available.";
            return result;
        }
        catch (Exception ex)
        {
            result.status = "cleanup_failed";
            result.error = UnwrapInvocationException(ex);
            return result;
        }
    }

    private TrainingBridgeBootstrapActionResult WriteImmediateProbeReport(
        TrainingActiveProbeReport report,
        string reportPrefix,
        string latestFileName,
        string statusKey)
    {
        report.completedAtUtc = DateTime.UtcNow;
        var fileName = $"{reportPrefix}_{report.timestampUtc:yyyyMMdd_HHmmss_fff}.json";
        var path = WriteProbeReport(fileName, latestFileName, report);
        _recordProbeStatus(statusKey, report.status, path);
        return new TrainingBridgeBootstrapActionResult
        {
            Succeeded = report.status != "failed",
            Status = report.status,
            ErrorCode = report.status == "failed" ? report.failureReason : null,
            ReportPath = path,
            Message = report.conclusion
        };
    }

    private string WriteProbeReport(string timestampedFileName, string latestFileName, TrainingActiveProbeReport report)
    {
        var path = _writeJson(timestampedFileName, report);
        _writeJson(latestFileName, report);
        return path;
    }

    private static bool IsLoadedSceneObject(GameObject gameObject)
    {
        if (gameObject == null)
        {
            return false;
        }

        try
        {
            return gameObject.scene.IsValid() && gameObject.scene.isLoaded;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDescendantOrSelf(Transform candidate, Transform root)
    {
        if (candidate == null || root == null)
        {
            return false;
        }

        var current = candidate;
        while (current != null)
        {
            if (current == root)
            {
                return true;
            }

            current = current.parent;
        }

        return false;
    }

    private static string SafePath(GameObject gameObject)
    {
        if (gameObject == null)
        {
            return null;
        }

        try
        {
            var parts = new Stack<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                parts.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", parts);
        }
        catch
        {
            return gameObject.name;
        }
    }

    private static string SafeSceneName(GameObject gameObject)
    {
        try
        {
            return gameObject != null && gameObject.scene.IsValid() ? gameObject.scene.name : null;
        }
        catch
        {
            return null;
        }
    }

    private static string SafeUnityObjectName(object value)
    {
        try
        {
            return value is UnityObject unityObject ? unityObject.name : value?.ToString();
        }
        catch
        {
            return value?.GetType().Name;
        }
    }

    private static List<string> SafeComponentTypeNames(GameObject gameObject)
    {
        try
        {
            return gameObject.GetComponents<Component>()
                .Where(component => component != null)
                .Select(component => component.GetType().FullName ?? component.GetType().Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(typeName => typeName, StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static string UnwrapInvocationException(Exception exception)
    {
        var current = exception;
        while (current is TargetInvocationException invocationException && invocationException.InnerException != null)
        {
            current = invocationException.InnerException;
        }

        return $"{current.GetType().Name}: {current.Message}";
    }

    private TrainingBridgeBootstrapActionResult CreateBusyResult()
    {
        var activeName = _activeSummonProbe?.Report?.probeName ??
                         _activeMoveProbe?.Report?.probeName ??
                         _activeDummyProbe?.Report?.probeName ??
                         _activeInteractionProbe?.Report?.probeName ??
                         "unknown";
        var activePath = _activeSummonProbe?.ReportPath ??
                         _activeMoveProbe?.ReportPath ??
                         _activeDummyProbe?.ReportPath ??
                         _activeInteractionProbe?.ReportPath;
        return new TrainingBridgeBootstrapActionResult
        {
            Succeeded = false,
            Status = "busy",
            ErrorCode = "probe_already_running",
            ReportPath = activePath,
            Message = $"A {activeName} probe is already running."
        };
    }

    private sealed class SummonCandidateSearch
    {
        public List<TrainingProbeCandidate> Records { get; set; } = new();
        public List<SummonCandidateRuntime> Eligible { get; set; } = new();
    }

    private sealed class SummonCandidateRuntime
    {
        public Component Component { get; set; }
        public MethodInfo Method { get; set; }
        public TrainingProbeCandidate Record { get; set; }
    }

    private sealed class ActiveSummonProbe
    {
        public string FileName { get; set; }
        public string ReportPath { get; set; }
        public TrainingActiveProbeReport Report { get; set; }
        public SceneObjectSnapshot Before { get; set; }
        public string ConfiguredPrefabName { get; set; }
        public float CompleteAtUnscaledTime { get; set; }
    }

    private sealed class MoveCandidateRuntime
    {
        public Component Component { get; set; }
        public MethodInfo MoveMethod { get; set; }
        public MethodInfo RepositionMethod { get; set; }
        public TrainingProbeCandidate Record { get; set; }
    }

    private sealed class ActiveMoveProbe
    {
        public string FileName { get; set; }
        public string ReportPath { get; set; }
        public TrainingActiveProbeReport Report { get; set; }
        public GameObject Actor { get; set; }
        public Component Component { get; set; }
        public MethodInfo MoveMethod { get; set; }
        public MethodInfo RepositionMethod { get; set; }
        public Vector3 OriginalPosition { get; set; }
        public Quaternion OriginalRotation { get; set; }
        public float CompleteAtUnscaledTime { get; set; }
    }

    private sealed class ActiveDummyProbe
    {
        public string FileName { get; set; }
        public string ReportPath { get; set; }
        public TrainingActiveProbeReport Report { get; set; }
        public GameObject Actor { get; set; }
        public Vector3 ActorStartPosition { get; set; }
        public GameObject Target { get; set; }
        public Vector3 TargetStartPosition { get; set; }
        public float CompleteAtUnscaledTime { get; set; }
    }

    private sealed class ActiveInteractionProbe
    {
        public string FileName { get; set; }
        public string ReportPath { get; set; }
        public TrainingActiveProbeReport Report { get; set; }
        public GameObject Target { get; set; }
        public Collider TargetCollider { get; set; }
        public TrainingProbeCollisionRecorder TargetRecorder { get; set; }
        public GameObject Projectile { get; set; }
        public Collider ProjectileCollider { get; set; }
        public Rigidbody ProjectileBody { get; set; }
        public TrainingProbeCollisionRecorder ProjectileRecorder { get; set; }
        public bool ColliderBoundsOverlapObserved { get; set; }
        public float CompleteAtUnscaledTime { get; set; }
    }

    private sealed class SceneObjectSnapshot
    {
        public Dictionary<int, TrainingProbeObjectState> States { get; } = new();
        public Dictionary<int, GameObject> Objects { get; } = new();
        public TrainingProbeSnapshotSummary Summary { get; set; } = new();
    }

    private sealed class ObservedProbeObject
    {
        public GameObject GameObject { get; set; }
        public bool IsNewInstance { get; set; }
        public TrainingProbeObservedObject Evidence { get; set; }
    }
}

internal sealed class TrainingActiveProbeReport
{
    public DateTime timestampUtc { get; set; }
    public DateTime? completedAtUtc { get; set; }
    public string reason { get; set; }
    public string probeName { get; set; }
    public string status { get; set; }
    public bool passiveOnly { get; set; }
    public string primaryActorPath { get; set; }
    public List<string> candidatePolicy { get; set; } = new();
    public List<string> requiredEvidence { get; set; } = new();
    public List<TrainingProbeCandidate> candidates { get; set; } = new();
    public List<TrainingProbeCandidate> modifierCandidates { get; set; } = new();
    public string modifierStatus { get; set; }
    public string modifierConclusion { get; set; }
    public TrainingProbeCandidate selectedCandidate { get; set; }
    public List<TrainingProbeAttempt> attempts { get; set; } = new();
    public TrainingProbeSnapshotSummary before { get; set; }
    public TrainingProbeSnapshotSummary after { get; set; }
    public TrainingProbeActorState beforeActor { get; set; }
    public TrainingProbeActorState afterActor { get; set; }
    public float observedDistance { get; set; }
    public TrainingProbeObjectState targetBefore { get; set; }
    public TrainingProbeObjectState targetAfter { get; set; }
    public TrainingProbeObjectState projectileBefore { get; set; }
    public TrainingProbeObjectState projectileAfter { get; set; }
    public float targetObservedDistance { get; set; }
    public float actorObservedDistance { get; set; }
    public bool fullSecondActor { get; set; }
    public bool dummyTarget { get; set; }
    public bool cloneAttempted { get; set; }
    public string cloneSkipReason { get; set; }
    public int collisionEnterCount { get; set; }
    public List<string> collisionOtherNames { get; set; } = new();
    public int triggerEnterCount { get; set; }
    public bool colliderBoundsOverlapObserved { get; set; }
    public List<string> contactOtherNames { get; set; } = new();
    public string interactionEvidenceLevel { get; set; }
    public bool damageEvidence { get; set; }
    public List<TrainingProbeObservedObject> observedObjects { get; set; } = new();
    public List<TrainingProbeCleanupResult> cleanup { get; set; } = new();
    public List<string> cleanupNotes { get; set; } = new();
    public string conclusion { get; set; }
    public string failureReason { get; set; }
}

internal sealed class TrainingProbeCandidate
{
    public string componentPath { get; set; }
    public string sceneName { get; set; }
    public string typeFullName { get; set; }
    public string memberName { get; set; }
    public string memberKind { get; set; }
    public string parameterSummary { get; set; }
    public string riskLevel { get; set; }
    public bool actorBound { get; set; }
    public bool activeInHierarchy { get; set; }
    public string configuredObjectName { get; set; }
    public bool eligible { get; set; }
    public string rejectionReason { get; set; }
}

internal sealed class TrainingProbeAttempt
{
    public DateTime attemptedAtUtc { get; set; }
    public string componentPath { get; set; }
    public string typeFullName { get; set; }
    public string memberName { get; set; }
    public string parameterSummary { get; set; }
    public string riskLevel { get; set; }
    public bool invoked { get; set; }
    public string outcome { get; set; }
    public string error { get; set; }
    public int observedObjectCount { get; set; }
}

internal sealed class TrainingProbeSnapshotSummary
{
    public int objectCount { get; set; }
    public int activeObjectCount { get; set; }
}

internal sealed class TrainingProbeObjectState
{
    public int instanceId { get; set; }
    public string name { get; set; }
    public string path { get; set; }
    public string sceneName { get; set; }
    public bool activeSelf { get; set; }
    public bool activeInHierarchy { get; set; }
    public TrainingProbeVector3 position { get; set; }
}

internal sealed class TrainingProbeObservedObject
{
    public int instanceId { get; set; }
    public string name { get; set; }
    public string path { get; set; }
    public string sceneName { get; set; }
    public string change { get; set; }
    public bool activeInHierarchy { get; set; }
    public TrainingProbeVector3 position { get; set; }
    public List<string> componentTypes { get; set; } = new();
    public bool structureComponentObserved { get; set; }
    public bool configuredPrefabNameMatched { get; set; }
}

internal sealed class TrainingProbeCleanupResult
{
    public int instanceId { get; set; }
    public string objectPath { get; set; }
    public string status { get; set; }
    public string cleanupMethod { get; set; }
    public string error { get; set; }
}

internal sealed class TrainingProbeVector3
{
    public float x { get; set; }
    public float y { get; set; }
    public float z { get; set; }

    public static TrainingProbeVector3 From(Vector3 value)
    {
        return new TrainingProbeVector3 { x = value.x, y = value.y, z = value.z };
    }
}

internal sealed class TrainingProbeActorState
{
    public string path { get; set; }
    public string sceneName { get; set; }
    public bool activeInHierarchy { get; set; }
    public TrainingProbeVector3 position { get; set; }
    public TrainingProbeVector3 rotationEuler { get; set; }
    public int frameCount { get; set; }
    public float unscaledTime { get; set; }
}
