# HALAS

**HALAS: On-Demand and AoI-Guided Explainability for Real-Time XR Analytics**

HALAS collects XR behavior evidence on a Unity client, reconstructs User Behavior Representations (UBRs) on an edge server, and presents behavior insights with their supporting evidence in a Web analyst console. A confidence gate can defer uncertain edge outputs to cloud language models and select a candidate through embedding-based semantic voting.

## Publication

**HALAS: On-Demand and AoI-Guided Explainability for Real-Time XR Analytics**

Boyang Li, Chongli Zhang, Chunsheng Wang, Ping Lu, Xiuquan Qiao, and Yakun Huang.

*IEEE Transactions on Visualization and Computer Graphics*, 2026, pp. 1-16.

- **DOI:** [10.1109/TVCG.2026.3747276](https://doi.org/10.1109/TVCG.2026.3747276)
- **IEEE Xplore:** [Article 11754050](https://ieeexplore.ieee.org/document/11754050)

## Source layout

```text
UnityProject/
  AoIGuideXR/
    Assets/Scripts/             VR/AR capture and action-evidence AoI filtering
    Assets/Editor/              Scene setup tools
    Assets/StreamingAssets/     Action-evidence calibration configuration
    Packages/manifest.json     Unity package dependencies
    ProjectSettings/           Unity editor version
  ExplainableXR/                Adapted logging, capture and WebSocket transport package
BehaviorProcessor/
  HALAS/Server/                UBR receiver, reconstruction and Web notification
  HALAS/LLM/                   Model API clients and semantic voting
  requirements.txt
  .env.example
Visual Analytics Interface/
  webApp/                     HTML, CSS, JavaScript and Three.js runtime
```

This is a source distribution. Model weights, checkpoints, datasets, captured geometry, recorded sessions, Unity scenes/prefabs, built applications and analysis reports are not included. The Unity sources must be integrated into a scene with the appropriate XR components; opening the project alone does not create a runnable XR scene.

## Unity client

Use Unity **2022.3.25f1c1** or a compatible Unity 2022.3 editor. Open `UnityProject/AoIGuideXR`; its manifest references `../ExplainableXR` through a relative local-package dependency. The manifest includes Input System 1.7.0, XR Interaction Toolkit 2.5.2, XR Hands 1.7.0 and AR Foundation/ARKit 5.1.6.

For PICO deployment, install the **PICO Unity Integration SDK 3.3.0** separately and enable its platform integration. The VR eye-tracking recorder uses the SDK's `Unity.XR.PXR` API. Configure the target platform, XR loader, Input System, cameras, controllers, microphone and scene references for your device. Device SDKs and their assets are not vendored here.

Add an `EdgeUploader` to a network GameObject and set its `wsUrl` to `ws://EDGE_HOST:8765/ws`. A headset must use the edge machine's reachable LAN address, not its own `127.0.0.1`. Add `AoIBayesianFilter` and the VR or AR behavior recorder to the scene, and assign the serialized camera, controller, interactor and filter references in the Inspector. Mark relevant scene objects with `AoITaskRelevant` as required by the task.

### Action-evidence AoI filtering

The runtime uses **12 one-time action-evidence tokens** and validates configuration array dimensions when loading. Each active `behaviorId` has its own posterior and set of applied evidence. `ObservedAction` carries observed facts; `CollectNovelEvidence` selects newly observed facts, and `ApplyEvidenceTokens` applies their category-conditional likelihoods in log space. Repeated samples of the same evidence do not repeatedly multiply the likelihood. A confirmed behavior remains selected until termination.

The packaged calibration has an AoI threshold of **0.786** and a trailing observation window of **0.5 seconds**. These are task-specific configuration values, not universal defaults for other tasks. The runtime checks a persistent calibrated configuration first, then the packaged calibrated configuration, then the packaged base configuration. Both packaged files contain the same complete action-evidence parameter set.

For a new task, calibrate the category priors, evidence likelihoods and retention threshold from your own annotated pilot trials. Count complete behaviors rather than sampling rows, and keep threshold selection separate from testing. Store the resulting parameters in `AoIFilterConfig.calibrated.json`. Collection utilities and benchmark scripts are excluded from the Unity runtime sources.

## Python edge server

Use Python **3.10 or later**. Create a virtual environment and install the dependencies:

```bash
python -m venv .venv
# Linux/macOS
source .venv/bin/activate
# Windows PowerShell: .venv\Scripts\Activate.ps1
python -m pip install -r BehaviorProcessor/requirements.txt
```

Install a PyTorch build suitable for your hardware. Whisper audio decoding also requires an FFmpeg executable on `PATH`. Enable optional GPU-dependent components only after configuring their local dependencies and model files.

Copy `BehaviorProcessor/.env.example` to `BehaviorProcessor/.env`. Populate the API keys for the cloud providers you use. The server loads this file without overriding existing environment variables. Keep `.env` private; it is ignored by Git.

```bash
python BehaviorProcessor/HALAS/Server/main.py
```

By default, Unity uploads are accepted on `0.0.0.0:8765`, and the analyst console receives notifications on port `8766`. Override these with `HALAS_WS_HOST`, `HALAS_WS_PORT` and `HALAS_FRONTEND_PORT`. Runtime files are created under `Visual Analytics Interface/webApp/obj/realtime/`; the Web server serves these paths. `HALAS_WEBAPP_ROOT` and `HALAS_REFERENCE_DIR` can override the Web root and local reference directory.

### Model services

Weights and inference servers are supplied separately. The edge processor calls these APIs:

| Component | Configuration | Request | Expected response |
| --- | --- | --- | --- |
| Context caption service | `HALAS_IMAGE2TEXT_API_URL` | Multipart form with `image`, `max_length`, `return_confidence` | JSON with `caption` |
| Edge insight service | `HALAS_TINY_MODEL_API_URL` | JSON with `ubr`, `max_new_tokens`, `return_logprobs` | JSON with `insight` and `content_token_logprobs` |
| Cloud language models | `QWEN_API_KEY`, `DEEPSEEK_API_KEY`, `LANYI_API_KEY` | Provider-specific requests in `llm_caller.py` | Candidate insights for semantic voting |

Set `HALAS_ENABLE_IMAGE2TEXT=1` and `HALAS_ENABLE_TINY_MODEL=1` only when the corresponding services are running. With the example's disabled defaults, the transport and interface can run, but model-generated captions and edge insights are unavailable. Missing services must not be interpreted as successful inference.

The confidence gate currently operates on **content-token log probabilities**. Its quantile is configured by `HALAS_TOKEN_ALPHA`, and its cutoff by `HALAS_TOKEN_THRESHOLD`; calibrate both against the deployed model's outputs. Do not use a probability-scale threshold directly as a log-probability cutoff.

AR object detection is optional. Install `ultralytics` and point `HALAS_YOLO_MODEL_PATH` at a local detector file. If that file is absent, detection is disabled rather than downloading weights automatically. Set `HALAS_YOLO_DEVICE=cpu` for CPU detection. Whisper and the semantic encoder may obtain their dependencies on first use; provision their model caches separately for offline deployments.

## Web analyst console

In a second terminal, serve the Web directory:

```bash
python -m http.server 8080 --directory "Visual Analytics Interface/webApp"
```

Open `http://localhost:8080/?edge=EDGE_HOST`. The `edge` parameter specifies the notification host; without it the console uses its page hostname. Use `?edge=EDGE_HOST&port=8766` if the notification port differs. The console opens a WebSocket connection, displays live UBRs and insights, and loads model URLs announced by the edge server. It starts empty and contains no prerecorded demonstration session.

The page loads its bundled Three.js runtime locally. Captured GLB files and context observations are generated at runtime and served from `obj/`; none are committed. For HTTPS hosting, expose the WebSocket endpoint over TLS through a reverse proxy.

## Third-party code

The adapted ExplainableXR package retains its original MIT license and attribution in `UnityProject/ExplainableXR/LICENSE`. The Web viewer includes Three.js and its loaders/controls under the license in `Visual Analytics Interface/webApp/vendor/three/LICENSE`. See `THIRD_PARTY_NOTICES.md` for source attribution.

## Citation

If you use HALAS in your research, please cite:

```bibtex
@article{li2026halas,
  author  = {Li, Boyang and Zhang, Chongli and Wang, Chunsheng and Lu, Ping and Qiao, Xiuquan and Huang, Yakun},
  title   = {{HALAS}: On-Demand and {AoI}-Guided Explainability for Real-Time {XR} Analytics},
  journal = {IEEE Transactions on Visualization and Computer Graphics},
  year    = {2026},
  pages   = {1--16},
  doi     = {10.1109/TVCG.2026.3747276},
  url     = {https://ieeexplore.ieee.org/document/11754050}
}
```
