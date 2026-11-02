window.HALASThreeStatus = "initializing";

const viewers = {
  referent: createViewer("referentViewer"),
  context: createViewer("contextViewer")
};

window.HALASThreeViews = {
  ready: true,
  setReferent(url, name) {
    loadModel(viewers.referent, url, name);
  },
  setContext(url, name) {
    loadModel(viewers.context, url, name);
  }
};
window.HALASThreeStatus = "ready";

window.addEventListener("halas-model-ready", (event) => {
  const { kind, url, name } = event.detail || {};
  if (!url) return;
  if (kind === "context") loadModel(viewers.context, url, name);
  else loadModel(viewers.referent, url, name);
});

window.dispatchEvent(new CustomEvent("halas-viewers-ready"));

function createViewer(containerId) {
  const container = document.getElementById(containerId);
  if (!container) return null;
  const kind = containerId === "referentViewer" ? "referent" : "context";

  const scene = new THREE.Scene();
  scene.background = new THREE.Color(0xf7fafc);

  const camera = new THREE.PerspectiveCamera(45, 1, 0.01, 1000);
  if (kind === "referent") {
    camera.position.set(0, 0.35, 2.4);
  } else {
    camera.position.set(1.4, 1.0, 1.8);
  }

  const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
  renderer.outputEncoding = THREE.sRGBEncoding;
  container.appendChild(renderer.domElement);

  const controls = new THREE.OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true;
  controls.dampingFactor = 0.08;
  controls.target.set(0, kind === "referent" ? 0 : 0.25, 0);
  controls.update();

  scene.add(new THREE.HemisphereLight(0xffffff, 0xb8c3cf, 1.1));
  const key = new THREE.DirectionalLight(0xffffff, 0.9);
  key.position.set(2, 3, 2);
  scene.add(key);

  const grid = new THREE.GridHelper(2.4, 12, 0xcbd5e1, 0xe2e8f0);
  scene.add(grid);

  const viewer = { container, scene, camera, renderer, controls, model: null, kind };
  resizeViewer(viewer);
  animate(viewer);

  window.addEventListener("resize", () => resizeViewer(viewer));
  return viewer;
}

async function loadModel(viewer, rawUrl, name) {
  if (!viewer) return;
  const url = normalizeUrl(rawUrl);
  clearModel(viewer);
  setPlaceholder(viewer.container, `Loading ${name || "model"}...`);
  console.info("[HALAS viewer] Loading model:", url);

  try {
    const loader = new THREE.GLTFLoader();
    const gltf = await loader.loadAsync(url);
    const model = gltf.scene;
    model.name = name || url;
    fitModel(model);
    viewer.scene.add(model);
    viewer.model = model;
    setPlaceholder(viewer.container, "");
    console.info("[HALAS viewer] Loaded model:", url);
  } catch (error) {
    setPlaceholder(viewer.container, `Unable to load ${name || "model"}: ${url}`);
    console.warn("[HALAS viewer] Model load failed:", url, error);
  }
}

function fitModel(model) {
  const box = new THREE.Box3().setFromObject(model);
  const size = new THREE.Vector3();
  const center = new THREE.Vector3();
  box.getSize(size);
  box.getCenter(center);
  model.position.sub(center);
  const maxAxis = Math.max(size.x, size.y, size.z) || 1;
  model.scale.multiplyScalar(1.1 / maxAxis);
}

function clearModel(viewer) {
  if (!viewer.model) return;
  viewer.scene.remove(viewer.model);
  viewer.model.traverse((node) => {
    if (node.geometry) node.geometry.dispose();
    if (node.material) {
      const materials = Array.isArray(node.material) ? node.material : [node.material];
      materials.forEach((material) => material.dispose());
    }
  });
  viewer.model = null;
}

function resizeViewer(viewer) {
  const rect = viewer.container.getBoundingClientRect();
  const width = Math.max(1, rect.width);
  const height = Math.max(1, rect.height);
  viewer.camera.aspect = width / height;
  viewer.camera.updateProjectionMatrix();
  viewer.renderer.setSize(width, height, false);
}

function animate(viewer) {
  requestAnimationFrame(() => animate(viewer));
  viewer.controls.update();
  viewer.renderer.render(viewer.scene, viewer.camera);
}

function normalizeUrl(url) {
  const text = String(url || "").replaceAll("\\", "/");
  if (/^(https?:|blob:|data:)/i.test(text)) return text;
  return text;
}

function setPlaceholder(container, text) {
  const placeholder = container.querySelector(".viewer-placeholder");
  if (!placeholder) return;
  placeholder.textContent = text;
  placeholder.style.display = text ? "block" : "none";
}
