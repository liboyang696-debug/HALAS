import * as d3 from "https://cdn.jsdelivr.net/npm/d3@7/+esm";
import * as THREE from "https://cdn.skypack.dev/three@0.132.2";
import { FBXLoader } from "https://cdn.skypack.dev/three@0.132.2/examples/jsm/loaders/FBXLoader.js";
import {
	GLTFLoader
} from "https://cdn.skypack.dev/three@0.132.2/examples/jsm/loaders/GLTFLoader.js";
import {
	OBJLoader
} from "https://cdn.skypack.dev/three@0.132.2/examples/jsm/loaders/OBJLoader.js";
import {
	OrbitControls
} from "https://cdn.skypack.dev/three@0.132.2/examples/jsm/controls/OrbitControls.js";
import {
	LineGeometry
} from 'https://cdn.skypack.dev/three@0.132.2/examples/jsm/lines/LineGeometry.js';
import {
	LineMaterial
} from 'https://cdn.skypack.dev/three@0.132.2/examples/jsm/lines/LineMaterial.js';
import {
	Line2
} from 'https://cdn.skypack.dev/three@0.132.2/examples/jsm/lines/Line2.js';

// ========== Realtime System Configuration ==========
const REALTIME_CONFIG = {
	enabled: true,
	backendWS: 'ws://localhost:8766',
	reconnectInterval: 3000
};

let realtimeWebSocket = null;
let currentRealtimeContextModel = null;
let currentRealtimeHandModel = null;
let realtimeLogEvents = [];

let logMode = {
	vrGame: 0,
	immersiveAnalytics: 0,
	infoVisCollab: 1,
	sceneNavigation: 0,
	maintenance: 0,
	videoScene: 0
};

let video = false;
const animationStep = 100;

let numUsers = 0;
let uniqueUsers;
let uniqueActions;
let dynamicColorMapping;
let x;
let globalState = {
	currentTimestamp: 0,
	bins: undefined,
	unit: "minutes",
	jsonDatas: [],
	avatars: [],
	meshes: [],
	interactionMeshes: [],
	speechMeshes: [],
	intervals: undefined,
	intervalDuration: 0,
	globalStartTime: 0,
	globalEndTime: 0,
	startTimeStamp: 0,
	endTimeStamp: 0,
	currentDataIndex: -1,
	show: Array(numUsers + 1).fill(true),
	lineTimeStamp1: 0,
	lineTimeStamp2: 0,
	finalData: undefined,
	dynamicWidth: 0,
	scene: undefined,
	camera: undefined,
	renderer: undefined,
	controls: undefined,
	currentLineSegments: [],
	triangleMesh: [],
	raycastLines: [],
	rightControls: [],
	leftControls: [],
	lineDrawing: [],
	currentInteractionObject: null,
	interactionObjectScene: null,
	interactionObjectCamera: null,
	interactionObjectRenderer: null,
	loadedClouds: {},
	loadedObjects: {},
	llmInsightData: {},
	heatmaps: [],
	useCase: "",
	logFIlePath: "",
	llmInsightPath: "",
	objFilePath: "",
	viewProps: {},
	obContext: [],
	markers: {},
	isAnimating: video
};

const configData = await Promise.all([
	fetch('config.json').then(response => response.json()),
]);

let selectedLogMode = Object.keys(logMode).find(key => logMode[key] === 1);
globalState.useCase = selectedLogMode;

if (selectedLogMode && configData[0][selectedLogMode]) {
	globalState.logFIlePath = configData[0][selectedLogMode].logFilePath;
	globalState.llmInsightPath = configData[0][selectedLogMode].llmInsightFilePath;
	globalState.objFilePath = configData[0][selectedLogMode].objFilePath;
	globalState.obContext = configData[0][selectedLogMode].obContext;
} else {
	console.log("No valid mode selected or key not found in JSON.");
}

function initializeViewProps() {
	globalState.viewProps = Object.fromEntries(
		Array.from({ length: numUsers + 1 }, (_, i) => [
			`User${i}`,
			// In realtime mode, enable Context, Object, Tracemap by default
			Object.fromEntries(globalState.obContext.map(context => {
				if (REALTIME_CONFIG.enabled && (context === 'Context' || context === 'Object' || context === 'Tracemap')) {
					return [context, true];
				}
				return [context, false];
			}))
		])
	);
}

const margin = { top: 20, right: 30, bottom: 5, left: 160 };
let buffer = 167;
const hsl = { h: 0, s: 0, l: 0 };
const userActionColors = {
	"User1": {
		"Action1": "#A8E6CF",
		"Action2": "#88DBC2",
		"Action3": "#66CFAE",
		"Action4": "#44C39A",
		"Action5": "#22B787",
		"Action6": "#1EA175"
	},
	"User2": {
		"Action1": "#4DD0E1",
		"Action2": "#42BACE",
		"Action3": "#36A1B8",
		"Action4": "#2A87A0",
		"Action5": "#1F6E88",
		"Action6": "#155571"
	},
	"User3": {
		"Action1": "#1E88E5",
		"Action2": "#1876CD",
		"Action3": "#1462B4",
		"Action4": "#104F9B",
		"Action5": "#0D3C82",
		"Action6": "#09296A"
	}
};

const colorScale = d3.scaleOrdinal()
	.domain(["User1", "User2", "User3", "0", "1", "2"])
	.range(["#66CFAE", "#36A1B8", "#4682b4", "#8dd3c7", "#fdcdac", "#bebada"]);

const opacities = [0.2, 0.4, 0.6, 0.8, 1];

function toggleAnimation() {
	if (video) {
		const isPlaying = playIcon.style.display === 'none';
		if (isPlaying) {
			pauseIcon.style.display = 'none';
			playIcon.style.display = 'block';
			globalState.isAnimating = false;
		} else {
			playIcon.style.display = 'none';
			pauseIcon.style.display = 'block';
			globalState.isAnimating = true;
		}
		if (globalState.isAnimating) {
			animateVisualization();
		}
	}
}

export function updateIntervals() {
	createSharedAxis();
	createLines(globalState.lineTimeStamp1, globalState.lineTimeStamp2);
	generateHierToolBar();
	createPlotTemporal();
	plotUserSpecificBarChart();
	plotUserSpecificDurationBarChart();
	updateObjectsBasedOnSelections();
	updateMarkersBasedOnSelections();
}

function changeBinSize(newBinSize) {
	const unit = document.querySelector('input[name="unit"]:checked').value;
	var event = new CustomEvent('binSizeChange', {
		detail: { size: newBinSize, unit: unit }
	});
	updateIntervals(newBinSize, unit);
	createPlotTemporal();
	window.dispatchEvent(event);
	callDrawBookmarks(globalState.llmInsightData);
}

document.getElementById('binsDropdown').addEventListener('change', function () {
	changeBinSize(this.value);
});

document.getElementById('unit-selection-container').addEventListener('change', function () {
	const unit = document.querySelector('input[name="unit"]:checked').value;
	const currentBinSize = document.getElementById('binsDropdown').value;
	changeBinSize(currentBinSize);
});

window.addEventListener('binSizeChange', function (e) {
	globalState.bins = e.detail.size;
	globalState.unit = e.detail.unit;
	updateIntervals(e.detail.size, e.detail.unit);
});

async function loadAvatarModel(filename) {
	const loader = new GLTFLoader();
	const gltf = await loader.loadAsync(filename);
	const avatar = gltf.scene;
	avatar.rotation.set(0, 0, 0);
	avatar.scale.set(1, 1, 1);
	avatar.name = filename;
	globalState.scene.add(avatar);
	return avatar;
}

async function loadIpadModel(filename) {
	const loader = new GLTFLoader();
	const gltf = await loader.loadAsync(filename);
	const avatar = gltf.scene;
	avatar.rotation.set(0, 0, 0);
	avatar.scale.set(2, 2, 2);
	avatar.name = filename;
	globalState.scene.add(avatar);
	return avatar;
}

async function loadHand(filename) {
	const loader = new GLTFLoader();
	const gltf = await loader.loadAsync(filename);
	const avatar = gltf.scene;
	avatar.scale.set(2, 2, 2);
	avatar.name = filename;
	globalState.scene.add(avatar);
	return avatar;
}

function bindUserCheckboxListeners() {
	for (let i = 1; i <= numUsers; i++) {
		const checkbox = document.getElementById(`toggle-user${i}`);
		if (!checkbox) continue;

		checkbox.addEventListener('change', function () {
			const userID = i;
			const userName = Array.from(uniqueUsers)[i - 1] || `User${i}`;
			globalState.show[userID] = this.checked;

			if (globalState.show[userID]) {
				if (globalState.currentLineSegments[userID]) {
					globalState.scene.add(globalState.currentLineSegments[userID]);
				}
				if (globalState.triangleMesh[userID]) {
					globalState.triangleMesh[userID].forEach(mesh => {
						globalState.scene.add(mesh);
					});
				}
				if (globalState.avatars[userID - 1]) {
					globalState.avatars[userID - 1].visible = true;
				}
				if (globalState.rightControls[userID - 1]) {
					globalState.rightControls[userID - 1].visible = true;
				}
				if (globalState.leftControls[userID - 1]) {
					globalState.leftControls[userID - 1].visible = true;
				}
				if (globalState.raycastLines[userID]) {
					globalState.raycastLines[userID].forEach(mesh => {
						globalState.scene.add(mesh);
					});
				}
				if (globalState.lineDrawing[userID]) {
					globalState.lineDrawing[0].forEach(filename => {
						const existingObject = globalState.scene.getObjectByName(filename);
						if (existingObject) {
							existingObject.visible = true;
						}
					});
				}
				if (currentRealtimeContextModel && currentRealtimeContextModel.userData.user === userName) {
					const isContextEnabled = globalState.viewProps[userName] && globalState.viewProps[userName]["Context"];
					if (isContextEnabled) {
						currentRealtimeContextModel.visible = true;
					}
				}
				if (currentRealtimeHandModel && currentRealtimeHandModel.userData.user === userName) {
					const isObjectEnabled = globalState.viewProps[userName] && globalState.viewProps[userName]["Object"];
					if (isObjectEnabled) {
						currentRealtimeHandModel.visible = true;
					}
				}
			} else {
				if (globalState.currentLineSegments[userID]) {
					globalState.scene.remove(globalState.currentLineSegments[userID]);
				}
				if (globalState.triangleMesh[userID]) {
					globalState.triangleMesh[userID].forEach(mesh => {
						globalState.scene.remove(mesh);
					});
				}
				if (globalState.avatars[userID - 1]) {
					globalState.avatars[userID - 1].visible = false;
				}
				if (globalState.rightControls[userID - 1]) {
					globalState.rightControls[userID - 1].visible = false;
				}
				if (globalState.leftControls[userID - 1]) {
					globalState.leftControls[userID - 1].visible = false;
				}
				if (globalState.raycastLines[userID]) {
					globalState.raycastLines[userID].forEach(mesh => {
						globalState.scene.remove(mesh);
					});
				}
				if (globalState.lineDrawing[userID]) {
					globalState.lineDrawing[0].forEach(filename => {
						const existingObject = globalState.scene.getObjectByName(filename);
						if (existingObject) {
							existingObject.visible = false;
						}
						globalState.scene.remove(existingObject);
					});
				}
				if (currentRealtimeContextModel && currentRealtimeContextModel.userData.user === userName) {
					currentRealtimeContextModel.visible = false;
				}
				if (currentRealtimeHandModel && currentRealtimeHandModel.userData.user === userName) {
					currentRealtimeHandModel.visible = false;
				}
			}
			refreshAllVisualization();
		});
	}
}

function window_onload() {
	generateUserLegends();
	bindUserCheckboxListeners();
}

function updateVisualization(nextTimestamp) {
	for (let i = 0; i < numUsers; i++) {
		updateUserDevice(i, nextTimestamp);
		updateLeftControl(i, nextTimestamp);
		updateRightControl(i, nextTimestamp);
	}
}

function updateUserDevice(userId, timestamp = null) {
	const userField = `User${userId + 1}`;
	let deviceType = '';

	if (logMode.vrGame || logMode.immersiveAnalytics) {
		deviceType = 'XRHMD';
	} else if (logMode.infoVisCollab || logMode.infoVisCollab1 || logMode.sceneNavigation || logMode.maintenance || logMode.videoScene) {
		deviceType = 'HandheldARInputDevice';
	} else {
		console.warn('Unsupported log mode.');
		return;
	}

	const navigateActions = globalState.finalData.filter(action =>
		action.TriggerSource === deviceType &&
		action.User === userField
	);

	const allSubActions = [];

	if (globalState.isAnimating && timestamp !== null) {
		navigateActions.forEach(action => {
			action.Data.forEach(subAction => {
				const invokeTime = parseTimeToMillisRobust(subAction.ActionInvokeTimestamp);
				if (invokeTime === timestamp) {
					allSubActions.push({
						parentAction: action,
						...subAction,
						Timestamp: invokeTime
					});
				}
			});
		});
	} else {
		navigateActions.forEach(action => {
			action.Data.forEach(subAction => {
				const invokeTime = parseTimeToMillisRobust(subAction.ActionInvokeTimestamp);
				if (invokeTime >= globalState.lineTimeStamp1 && invokeTime <= globalState.lineTimeStamp2) {
					allSubActions.push({
						parentAction: action,
						...subAction,
						Timestamp: invokeTime
					});
				}
			});
		});
	}

	allSubActions.sort((a, b) => a.Timestamp - b.Timestamp);

	allSubActions.forEach(subAction => {
		const location = parseLocation(subAction.ActionInvokeLocation);
		if (globalState.avatars[userId]) {
			globalState.avatars[userId].position.set(location.x, location.y, location.z);
			const euler = new THREE.Euler(
				THREE.MathUtils.degToRad(location.pitch),
				THREE.MathUtils.degToRad(location.yaw),
				THREE.MathUtils.degToRad(location.roll),
				'XYZ'
			);
			globalState.avatars[userId].rotation.set(0, 0, 0);
			globalState.avatars[userId].setRotationFromEuler(euler);
		}
	});
}

function updateLeftControl(userId, timestamp = null) {
	const userField = `User${userId + 1}`;
	let actionName, deviceType;

	if (logMode.immersiveAnalytics) {
		actionName = 'Move Hand';
		deviceType = 'XRHand_L';
	} else if (logMode.vrGame) {
		actionName = 'Move Controller';
		deviceType = 'XRController_L';
	} else {
		return;
	}

	const navigateActions = globalState.finalData.filter(action =>
		action.TriggerSource === deviceType &&
		action.User === userField
	);

	const allSubActions = [];

	if (globalState.isAnimating && timestamp !== null) {
		navigateActions.forEach(action => {
			action.Data.forEach(subAction => {
				const invokeTime = parseTimeToMillisRobust(subAction.ActionInvokeTimestamp);
				if (invokeTime === timestamp) {
					allSubActions.push({
						parentAction: action,
						...subAction,
						Timestamp: invokeTime
					});
				}
			});
		});
	} else {
		navigateActions.forEach(action => {
			action.Data.forEach(subAction => {
				const invokeTime = parseTimeToMillisRobust(subAction.ActionInvokeTimestamp);
				if (invokeTime >= globalState.lineTimeStamp1 && invokeTime <= globalState.lineTimeStamp2) {
					allSubActions.push({
						parentAction: action,
						...subAction,
						Timestamp: invokeTime
					});
				}
			});
		});
	}

	allSubActions.sort((a, b) => a.Timestamp - b.Timestamp);

	allSubActions.forEach(subAction => {
		const location = parseLocation(subAction.ActionInvokeLocation);
		if (globalState.leftControls[userId]) {
			globalState.leftControls[userId].position.set(location.x, location.y, location.z);
			const euler = new THREE.Euler(
				THREE.MathUtils.degToRad(location.pitch),
				THREE.MathUtils.degToRad(location.yaw),
				THREE.MathUtils.degToRad(location.roll),
				'XYZ'
			);
			globalState.leftControls[userId].rotation.set(0, 0, 0);
			globalState.leftControls[userId].setRotationFromEuler(euler);
		}
	});
}

function updateRightControl(userId, timestamp = null) {
	const userField = `User${userId + 1}`;
	let actionName, deviceType;

	if (logMode.immersiveAnalytics) {
		actionName = 'Move Hand';
		deviceType = 'XRHand_R';
	} else if (logMode.vrGame) {
		actionName = 'Move Controller';
		deviceType = 'XRController_R';
	} else {
		return;
	}

	const navigateActions = globalState.finalData.filter(action =>
		action.TriggerSource === deviceType &&
		action.User === userField
	);

	const allSubActions = [];

	if (globalState.isAnimating && timestamp !== null) {
		navigateActions.forEach(action => {
			action.Data.forEach(subAction => {
				const invokeTime = parseTimeToMillisRobust(subAction.ActionInvokeTimestamp);
				if (invokeTime === timestamp) {
					allSubActions.push({
						parentAction: action,
						...subAction,
						Timestamp: invokeTime
					});
				}
			});
		});
	} else {
		navigateActions.forEach(action => {
			action.Data.forEach(subAction => {
				const invokeTime = parseTimeToMillisRobust(subAction.ActionInvokeTimestamp);
				if (invokeTime >= globalState.lineTimeStamp1 && invokeTime <= globalState.lineTimeStamp2) {
					allSubActions.push({
						parentAction: action,
						...subAction,
						Timestamp: invokeTime
					});
				}
			});
		});
	}

	allSubActions.sort((a, b) => a.Timestamp - b.Timestamp);

	allSubActions.forEach(subAction => {
		const location = parseLocation(subAction.ActionInvokeLocation);
		if (globalState.rightControls[userId]) {
			globalState.rightControls[userId].position.set(location.x, location.y, location.z);
			const euler = new THREE.Euler(
				THREE.MathUtils.degToRad(location.pitch),
				THREE.MathUtils.degToRad(location.yaw),
				THREE.MathUtils.degToRad(location.roll),
				'XYZ'
			);
			globalState.rightControls[userId].rotation.set(0, 0, 0);
			globalState.rightControls[userId].setRotationFromEuler(euler);
		}
	});
}

function calculateDistance(point1, point2) {
	return Math.sqrt(
		Math.pow(point1.x - point2.x, 2) +
		Math.pow(point1.y - point2.y, 2) +
		Math.pow(point1.z - point2.z, 2)
	);
}

function generateDynamicColorMapping(uniqueUsers, uniqueActions) {
	const dynamicColorMapping = {};
	uniqueUsers.forEach(user => {
		dynamicColorMapping[user] = {};
		uniqueActions.forEach((action, index) => {
			const actionKey = `Action${index + 1}`;
			if (userActionColors[user] && userActionColors[user][actionKey]) {
				dynamicColorMapping[user][action] = userActionColors[user][actionKey];
			}
		});
	});
	return dynamicColorMapping;
}

function getColorForUserAction(userID, actionName) {
	return (dynamicColorMapping[userID] && dynamicColorMapping[userID][actionName]) || '#ffffff';
}

function updatePointCloudBasedOnSelections() {
	const data = globalState.finalData;
	const newFilteredActions = {};
	const hasVisibleUserID = Object.keys(globalState.show)
		.filter((userID) => globalState.show[userID])
		.map((userID) => `User${userID}`);
	const visibleContextUsers = hasVisibleUserID.filter(userId => {
		return userId in globalState.viewProps && globalState.viewProps[userId]["Context"] === true;
	});
	const nonVisibleContextUsers = hasVisibleUserID.filter(userId => {
		return userId in globalState.viewProps && globalState.viewProps[userId]["Context"] === false;
	});

	if (currentRealtimeContextModel && currentRealtimeContextModel.userData.user) {
		const modelUser = currentRealtimeContextModel.userData.user;
		const shouldShow = visibleContextUsers.includes(modelUser);
		currentRealtimeContextModel.visible = shouldShow;
	}

	if (currentRealtimeContextModel && currentRealtimeContextModel.userData.user) {
		const modelUser = currentRealtimeContextModel.userData.user;
		if (!hasVisibleUserID.includes(modelUser)) {
			currentRealtimeContextModel.visible = false;
		}
	}

	nonVisibleContextUsers.forEach((nvcUser) => {
		if (nvcUser in globalState.loadedClouds) {
			Object.keys(globalState.loadedClouds[nvcUser]).forEach(key => {
				const obj = globalState.scene.getObjectByName(key);
				if (obj) {
					globalState.scene.remove(obj);
					delete globalState.loadedClouds[nvcUser][key];
				}
			});
		}
	});

	visibleContextUsers.forEach((vcUser) => {
		newFilteredActions[vcUser] = new Set();
	});

	const filteredActions = data.filter(action => {
		const hasVisibleUserID = visibleContextUsers.some(userID => action.User.includes(userID));
		return hasVisibleUserID && action.Data.some(subAction => {
			const actionStartTime = parseTimeToMillisRobust(subAction.ActionInvokeTimestamp);
			const actionEndTime = actionStartTime + parseDurationToMillis(action.Duration);
			if (actionEndTime >= globalState.lineTimeStamp1 && actionStartTime <= globalState.lineTimeStamp2 && subAction.ActionContext) {
				newFilteredActions[action.User].add(`${globalState.objFilePath}${subAction.ActionContext}`);
				return true;
			}
			return false;
		});
	});

	Object.keys(globalState.loadedClouds).forEach(userId => {
		Object.keys(globalState.loadedClouds[userId]).forEach(key => {
			const obj = globalState.scene.getObjectByName(key);
			if (obj && !newFilteredActions[userId].has(key)) {
				globalState.scene.remove(obj);
				delete globalState.loadedClouds[userId][key];
			}
		});
	});

	if (hasVisibleUserID.length === 0 || visibleContextUsers.length === 0) {
		return;
	}

	for (const action of filteredActions) {
		for (const subAction of action.Data) {
			if (subAction.ActionContext !== null && globalState.loadedClouds?.[action.User]?.[subAction.ActionContext] === undefined
				&& subAction.ActionInvokeLocation !== null) {
				const adjustedPath = `${globalState.objFilePath}${subAction.ActionContext}`;
				if (globalState.loadedClouds[action.User] === undefined) {
					globalState.loadedClouds[action.User] = {};
				}
				if (!globalState.loadedClouds[action.User].hasOwnProperty(adjustedPath)) {
					globalState.loadedClouds[action.User][adjustedPath] = loadAvatarModel(adjustedPath)
						.then(obj => {
							obj.name = adjustedPath;
							globalState.scene.add(obj);
							return obj;
						})
						.catch(error => {
							console.error(`Failed to load object: ${error}`);
							delete globalState.loadedClouds[action.User][adjustedPath];
						});
				}
			}
		}
	}
}

async function updateMarkersBasedOnSelections() {
	const data = globalState.finalData;
	const newFilteredActions = {};
	const selectedActions = getSelectedTopics();

	const selectedUsers = Object.keys(globalState.show)
		.filter(userID => globalState.show[userID])
		.map(userID => `User${userID}`);

	const visibleObjectUsers = selectedUsers.filter(userId => {
		return userId in globalState.viewProps && globalState.viewProps[userId]["Tracemap"] === true;
	});

	visibleObjectUsers.forEach((vcUser) => {
		newFilteredActions[vcUser] = [];
	});

	for (const action of data) {
		if (!action || !action.Data || !Array.isArray(action.Data)) continue;

		for (const subAction of action.Data) {
			if (!subAction || !subAction.ActionInvokeTimestamp) continue;

			const actionStartTime = parseTimeToMillisRobust(subAction.ActionInvokeTimestamp);
			const actionEndTime = actionStartTime + parseDurationToMillis(action.Duration);

			if (
				actionEndTime >= globalState.lineTimeStamp1 &&
				actionStartTime <= globalState.lineTimeStamp2 &&
				selectedActions.includes(action.Name) &&
				visibleObjectUsers.includes(action.User)
			) {
				newFilteredActions[action.User].push({ subAction, action });
			}
		}
	}

	for (const userID of Object.keys(globalState.markers || {})) {
		for (const key of Object.keys(globalState.markers[userID] || {})) {
			const markerExists = newFilteredActions[userID]?.some(filteredAction => {
				const { subAction } = filteredAction;
				const keyForCheck = `${subAction.ActionInvokeTimestamp}_${subAction.ActionReferentLocation}`;
				return keyForCheck === key;
			});

			if (!markerExists) {
				const markerGroup = globalState.markers[userID][key];
				if (markerGroup) {
					globalState.scene.remove(markerGroup);
					markerGroup.children.forEach(child => {
						if (child.geometry) child.geometry.dispose();
						if (child.material) child.material.dispose();
					});
					markerGroup.clear();
					delete globalState.markers[userID][key];
				}
			}
		}
	}

	for (const userID of Object.keys(newFilteredActions)) {
		newFilteredActions[userID].forEach(({ subAction, action }) => {
			const key = `${subAction.ActionInvokeTimestamp}_${subAction.ActionInvokeLocation}`;

			if (!globalState.markers) globalState.markers = {};
			if (!globalState.markers[userID]) globalState.markers[userID] = {};

			if (!globalState.markers[userID][key]) {
				const location = parseLocation(subAction.ActionInvokeLocation);

				let tooClose = false;
				if (action.Name === "Navigate") {
					for (const existingMarkerKey of Object.keys(globalState.markers[userID])) {
						const existingMarker = globalState.markers[userID][existingMarkerKey];
						const existingPosition = existingMarker.position;
						const distance = calculateDistance(location, existingPosition);

						if (distance < 0.1) {
							tooClose = true;
							break;
						}
					}
				}

				if (!tooClose) {
					const marker = createSphereMarker(userID, action.Name);
					marker.position.set(location.x, location.y, location.z);
					marker.name = `Marker_${key}`;
					globalState.scene.add(marker);
					globalState.markers[userID][key] = marker;
				}
			}
		});
	}
}

async function updateObjectsBasedOnSelections() {
	const data = globalState.finalData;
	const newFilteredActions = {};
	const actionsToLoad = {};
	const selectedActions = getSelectedTopics();

	const selectedUsers = Object.keys(globalState.show)
		.filter(userID => globalState.show[userID])
		.map(userID => `User${userID}`);

	const visibleObjectUsers = selectedUsers.filter(userId => {
		return userId in globalState.viewProps && globalState.viewProps[userId]["Object"] === true;
	});

	const nonVisibleObjectUsers = selectedUsers.filter(userId => {
		return userId in globalState.viewProps && globalState.viewProps[userId]["Object"] === false;
	});

	const allVisibleUsers = Object.keys(globalState.show)
		.filter(userID => globalState.show[userID])
		.map(userID => Array.from(uniqueUsers)[userID - 1] || `User${userID}`);

	if (currentRealtimeHandModel && currentRealtimeHandModel.userData.user) {
		const modelUser = currentRealtimeHandModel.userData.user;
		const shouldShow = visibleObjectUsers.includes(modelUser);
		currentRealtimeHandModel.visible = shouldShow;
	}

	if (currentRealtimeHandModel && currentRealtimeHandModel.userData.user) {
		const modelUser = currentRealtimeHandModel.userData.user;
		if (!allVisibleUsers.includes(modelUser)) {
			currentRealtimeHandModel.visible = false;
		}
	}

	for (const nvoUser of nonVisibleObjectUsers) {
		if (nvoUser in globalState.loadedObjects) {
			for (const key of Object.keys(globalState.loadedObjects[nvoUser])) {
				if (globalState.loadedObjects[nvoUser][key]) {
					try {
						const obj = await globalState.loadedObjects[nvoUser][key];
						while (obj && obj.parent) {
							if (obj.geometry) obj.geometry.dispose();
							if (obj.material) obj.material.dispose();
							globalState.scene.remove(obj);
						}
						delete globalState.loadedObjects[nvoUser][key];
					} catch (error) {
						console.error(`Error removing object ${key}:`, error);
					}
				}
			}
		}
	}

	visibleObjectUsers.forEach((vcUser) => {
		newFilteredActions[vcUser] = new Set();
		actionsToLoad[vcUser] = [];
	});

	for (const action of data) {
		if (action.Data.length === 0) continue;

		let originLocation = null;
		if (action.Data.length > 1 && action.ReferentType == "Virtual") {
			const firstDataEntry = action.Data[0];
			if (firstDataEntry.ActionReferentLocation) {
				originLocation = parseLocation(firstDataEntry.ActionReferentLocation);
			}
		}

		for (const subAction of action.Data) {
			const actionStartTime = parseTimeToMillisRobust(subAction.ActionInvokeTimestamp);
			const actionEndTime = actionStartTime + parseDurationToMillis(action.Duration);

			if (actionEndTime >= globalState.lineTimeStamp1 && actionStartTime <= globalState.lineTimeStamp2 &&
				subAction.ActionReferentBody && action.ReferentType === "Virtual" &&
				selectedActions.includes(action.Name) && visibleObjectUsers.includes(action.User)) {

				const key = `${subAction.ActionInvokeTimestamp}_${subAction.ActionReferentBody}`;
				if (!newFilteredActions[action.User].has(key)) {
					newFilteredActions[action.User].add(key);
					actionsToLoad[action.User].push({ key, subAction, originLocation });
				}
			}
		}
	}

	// Unload objects that are no longer needed
	for (const userID of Object.keys(globalState.loadedObjects)) {
		for (const key of Object.keys(globalState.loadedObjects[userID])) {
			if (!newFilteredActions[userID].has(key) && globalState.loadedObjects[userID][key]) {
				const obj = await globalState.loadedObjects[userID][key];
				if (obj && obj.parent) {
					if (obj.geometry) obj.geometry.dispose();
					if (obj.material) obj.material.dispose();
					globalState.scene.remove(obj);
					delete globalState.loadedObjects[userID][key];
				}
			}
		}
	}

	if (selectedUsers.length === 0 || visibleObjectUsers.length === 0) {
		return;
	}

	// Load new objects that are required
	for (const userID of Object.keys(actionsToLoad)) {
		for (const { key, subAction, originLocation } of actionsToLoad[userID]) {
			if (globalState.loadedObjects[userID] === undefined) {
				globalState.loadedObjects[userID] = {};
			}
			if (!globalState.loadedObjects?.[userID]?.[key]) {
				if (!globalState.loadedObjects[userID].hasOwnProperty(key)) {
					const adjustedPath = `${globalState.objFilePath}${subAction.ActionReferentBody}`;
					globalState.loadedObjects[userID][key] = loadAvatarModel(adjustedPath)
						.then(obj => {
							obj.name = key;
							const location = parseLocation(subAction.ActionReferentLocation);
							if (logMode.videoScene) {
								obj.position.y += 0.25;
							}
							if (logMode.vrGame && originLocation) {
								const existingObject = globalState.scene.getObjectByName(key);
								const deltaX = location.x - originLocation.x;
								const deltaY = location.y - originLocation.y;
								const deltaZ = location.z - originLocation.z;
								if (existingObject) {
									existingObject.position.set(deltaX, deltaY, deltaZ);
								}
							}
							return obj;
						})
						.catch(error => {
							console.error(`Failed to load object ${key}:`, error);
							delete globalState.loadedObjects[userID][key];
						});
				}
			}
		}
	}
}

function createSphereMarker(userID, actionName) {
	const radius = 0.03;
	const widthSegments = 32;
	const heightSegments = 32;
	const color = getColorForUserAction(userID, actionName);
	const sphereGeometry = new THREE.SphereGeometry(radius, widthSegments, heightSegments);
	const markerMaterial = new THREE.MeshBasicMaterial({
		color: color,
		transparent: false,
		opacity: 0.8
	});
	const sphereMesh = new THREE.Mesh(sphereGeometry, markerMaterial);
	return sphereMesh;
}

function plotHeatmap() {
	return;

	const hasVisibleUserID = Object.keys(globalState.show)
		.filter((userID) => globalState.show[userID])
		.map((userID) => `User${userID}`);

	const visibleHeatmapUsers = hasVisibleUserID.filter(userId => {
		return userId in globalState.viewProps && globalState.viewProps[userId]["Heatmap"] === true;
	});

	Object.keys(globalState.heatmaps).forEach(userId => {
		const userHeatmap = globalState.heatmaps[userId];
		if (userHeatmap) {
			userHeatmap.forEach(heatmap => {
				if (heatmap.mesh) {
					globalState.scene.remove(heatmap.mesh);
				}
			});
		}
	});

	if (hasVisibleUserID.length === 0 || visibleHeatmapUsers.length === 0) {
		return;
	}

	globalState.heatmaps = {};
	visibleHeatmapUsers.forEach((vhi) => {
		globalState.heatmaps[vhi] = [];
	});

	const selectedActions = getSelectedTopics();
	const data = globalState.finalData;
	const gridSize = 50;
	const voxelSize = 0.1;

	const gridHelper = new THREE.GridHelper(gridSize * voxelSize, gridSize, 0x888888, 0x444444);
	gridHelper.position.set((gridSize * voxelSize) / 2, 0, (gridSize * voxelSize) / 2);

	visibleHeatmapUsers.forEach((user, userIndex) => {
		const heatmap = new Array(gridSize)
			.fill()
			.map(() =>
				new Array(gridSize)
					.fill()
					.map(() => new Array(gridSize).fill(0))
			);

		const actionsToDisplay = data.filter((action) => {
			return (
				action.User === user &&
				selectedActions.includes(action.Name) &&
				action.Data.some((subAction) => {
					const actionStartTime = parseTimeToMillisRobust(subAction.ActionInvokeTimestamp);
					const actionEndTime = actionStartTime + parseDurationToMillis(action.Duration);
					return (
						actionEndTime >= globalState.lineTimeStamp1 &&
						actionStartTime <= globalState.lineTimeStamp2
					);
				})
			);
		});

		actionsToDisplay.forEach((action) => {
			action.Data.forEach((subAction) => {
				const location = parseLocation(subAction.ActionInvokeLocation);
				if (location) {
					const gx = Math.floor(location.x / voxelSize);
					const gy = Math.floor(location.y / voxelSize);
					const gz = Math.floor(location.z / voxelSize);
					if (
						gx >= 0 && gx < gridSize &&
						gy >= 0 && gy < gridSize &&
						gz >= 0 && gz < gridSize
					) {
						heatmap[gx][gy][gz] += 1;
					}
				}
			});
		});

		const smoothedHeatmap = applyGaussianBlur3D(heatmap);
		renderHeatmap(heatmap, user, voxelSize);
	});
}

function renderHeatmap(heatmap, user, voxelSize) {
	const group = new THREE.Group();
	for (let x = 0; x < heatmap.length; x++) {
		for (let y = 0; y < heatmap[x].length; y++) {
			for (let z = 0; z < heatmap[x][y].length; z++) {
				const intensity = heatmap[x][y][z];
				if (intensity > 0) {
					const userColor = colorScale(user);
					const color = new THREE.Color(userColor);
					const material = new THREE.MeshBasicMaterial({
						color,
						transparent: true,
						opacity: Math.min(1, intensity / 5),
					});
					const cube = new THREE.Mesh(
						new THREE.SphereGeometry(voxelSize / 2),
						material
					);
					cube.position.set(
						x * voxelSize,
						y * voxelSize,
						z * voxelSize
					);
					group.add(cube);
				}
			}
		}
	}
	globalState.scene.add(group);
	globalState.heatmaps[user].push({ mesh: group });
}

function applyGaussianBlur3D(heatmap) {
	const kernelSize = 5;
	const sigma = 2.0;
	const kernel = createGaussianKernel(kernelSize, sigma);
	const smoothedHeatmap = JSON.parse(JSON.stringify(heatmap));
	for (let x = 0; x < heatmap.length; x++) {
		for (let y = 0; y < heatmap[x].length; y++) {
			for (let z = 0; z < heatmap[x][y].length; z++) {
				let sum = 0;
				let weightSum = 0;
				for (let i = -1; i <= 1; i++) {
					for (let j = -1; j <= 1; j++) {
						for (let k = -1; k <= 1; k++) {
							const nx = x + i;
							const ny = y + j;
							const nz = z + k;
							if (
								nx >= 0 && nx < heatmap.length &&
								ny >= 0 && ny < heatmap[x].length &&
								nz >= 0 && nz < heatmap[x][y].length
							) {
								const weight = kernel[i + 1][j + 1][k + 1];
								sum += heatmap[nx][ny][nz] * weight;
								weightSum += weight;
							}
						}
					}
				}
				smoothedHeatmap[x][y][z] = sum / weightSum;
			}
		}
	}
	return smoothedHeatmap;
}

function createGaussianKernel(size, sigma) {
	const kernel = new Array(size).fill().map(() => new Array(size).fill().map(() => new Array(size).fill(0)));
	const mean = Math.floor(size / 2);
	let sum = 0.0;
	for (let x = 0; x < size; x++) {
		for (let y = 0; y < size; y++) {
			for (let z = 0; z < size; z++) {
				const exponent = -(
					((x - mean) ** 2 + (y - mean) ** 2 + (z - mean) ** 2) /
					(2 * sigma ** 2)
				);
				kernel[x][y][z] = Math.exp(exponent);
				sum += kernel[x][y][z];
			}
		}
	}
	for (let x = 0; x < size; x++) {
		for (let y = 0; y < size; y++) {
			for (let z = 0; z < size; z++) {
				kernel[x][y][z] /= sum;
			}
		}
	}
	return kernel;
}

function parseLocation(locationString) {
	const parts = locationString.split(',');
	if (parts.length !== 6) {
		console.error('Invalid location format:', locationString);
		return null;
	}
	return {
		x: -parseFloat(parts[0]),
		y: parseFloat(parts[1]),
		z: parseFloat(parts[2]),
		pitch: parseFloat(parts[3]),
		yaw: -parseFloat(parts[4]),
		roll: parseFloat(parts[5])
	};
}

async function initializeScene() {
	globalState.scene = new THREE.Scene();
	globalState.scene.background = new THREE.Color(0xF5F5F5);
	const spatialView = document.getElementById('spatial-view');
	globalState.camera = new THREE.PerspectiveCamera(40, spatialView.innerWidth / spatialView.innerHeight, 0.1, 1000);
	globalState.camera.position.set(1, 3, 7);

	globalState.renderer = new THREE.WebGLRenderer({ antialias: true });
	globalState.renderer.setSize(spatialView.width, spatialView.height);
	globalState.renderer.toneMapping = THREE.LinearToneMapping;
	document.getElementById('spatial-view').appendChild(globalState.renderer.domElement);

	globalState.controls = new OrbitControls(globalState.camera, globalState.renderer.domElement);
	globalState.controls.enableZoom = true;

	const ambientLight = new THREE.AmbientLight(0xffffff, 1.0);
	globalState.scene.add(ambientLight);
	const directionalLight = new THREE.DirectionalLight(0xffffff, 0.8);
	directionalLight.position.set(0, 1, 0);
	globalState.scene.add(directionalLight);

	globalState.controls.update();

	const gridHelper = new THREE.GridHelper(10, 10);
	gridHelper.position.y = -1;

	// In realtime mode, start with empty data
	if (REALTIME_CONFIG.enabled) {
		console.log('[Realtime] Enabled, starting with empty data');
		globalState.finalData = [];
		globalState.llmInsightData = {};
	} else {
		const finalData = await Promise.all([
			fetch(globalState.logFIlePath).then(response => response.json()),
		]);
		globalState.finalData = finalData[0];
	}

	updateNumUsers();
	initializeViewProps();
	window_onload();
	const isHeadsetMode = logMode.vrGame || logMode.immersiveAnalytics;
	const avatarModel = isHeadsetMode ? 'headset.glb' : 'ipad.glb';
	const loadModel = isHeadsetMode ? loadAvatarModel : loadIpadModel;
	const avatarPromises = Array.from({ length: numUsers }, () => loadModel(avatarModel));

	globalState.avatars = await Promise.all(avatarPromises);
	const controlModels = {
		vrGame: { right: "controller_r.glb", left: "controller_l.glb" },
		immersiveAnalytics: { right: "hand_r.glb", left: "hand_l.glb" },
	};

	let rightControlModel, leftControlModel;

	if (logMode.vrGame) {
		({ right: rightControlModel, left: leftControlModel } = controlModels.vrGame);
	} else if (logMode.immersiveAnalytics) {
		({ right: rightControlModel, left: leftControlModel } = controlModels.immersiveAnalytics);
	} else {
		globalState.rightControls = [];
		globalState.leftControls = [];
	}

	if (rightControlModel && leftControlModel) {
		const rightControlPromises = Array.from({ length: numUsers }, () => loadHand(rightControlModel));
		const leftControlPromises = Array.from({ length: numUsers }, () => loadHand(leftControlModel));
		globalState.rightControls = await Promise.all(rightControlPromises);
		globalState.leftControls = await Promise.all(leftControlPromises);
	}

	setTimes(globalState.finalData);
}

// Get visible users' data based on checked checkboxes
function getVisibleUsersData() {
	const visibleUsers = Object.keys(globalState.show)
		.filter(userID => globalState.show[userID])
		.map(userID => Array.from(uniqueUsers)[userID - 1] || `User${userID}`);

	return globalState.finalData.filter(action => {
		const actionUser = action.User || (action.Data && action.Data[0] && action.Data[0].User);
		return visibleUsers.includes(actionUser);
	});
}

function createPlotTemporal() {
	if (!globalState.finalData || !Array.isArray(globalState.finalData) || globalState.finalData.length === 0) {
		return;
	}

	const visibleData = getVisibleUsersData();
	if (visibleData.length === 0) {
		const speechPlotSvg = d3.select("#speech-plot-container");
		speechPlotSvg.html("");
		return;
	}

	const topicsData = visibleData.map(action => {
		if (action && action.Data && action.Data.length > 0) {
			const firstEvent = action.Data[0];
			const startTimeMillis = parseTimeToMillisRobust(firstEvent.ActionInvokeTimestamp);
			const endTimeMillis = startTimeMillis + parseDurationToMillis(action.Duration);
			return {
				topic: action.Name,
				startTime: startTimeMillis,
				endTime: endTimeMillis,
				isUserInterest: false,
				hasUserInterestAction: false
			};
		}
		return null;
	}).filter(action => action !== null && action !== undefined && action.startTime && action.endTime);

	const temporalViewContainer = d3.select("#temporal-view");
	const width = (document.getElementById('spatial-view').clientWidth - margin.left - margin.right) * 0.9;
	const height = 310;
	const speechPlotSvg = d3.select("#speech-plot-container");
	speechPlotSvg.html("");
	const svg = speechPlotSvg.append('svg')
		.attr("width", globalState.dynamicWidth + margin.left + margin.right)
		.attr('height', margin.top + margin.bottom + height)
		.append('g')
		.attr('transform', `translate(${margin.left}, ${margin.top})`);

	const x = d3.scaleTime()
		.domain([d3.min(topicsData, d => d.startTime), d3.max(topicsData, d => d.endTime)])
		.range([0, globalState.dynamicWidth]);

	const yScale = d3.scaleBand()
		.domain(topicsData.map(d => d.topic))
		.rangeRound([0, height])
		.padding(0.1);

	const yAxis = svg.append("g")
		.attr("class", "axis axis--y")
		.call(d3.axisLeft(yScale));

	yAxis.selectAll("text")
		.style("font-size", "14px");

	const densityData = topicsData.map(d => {
		const totalDuration = d.endTime - d.startTime;
		const density = totalDuration / (globalState.lineTimeStamp2 - globalState.lineTimeStamp1);
		return { ...d, density };
	});

	const colorScale = d3.scaleSequential(d3.interpolateBlues)
		.domain([0, d3.max(densityData, d => d.density * 0.6)]);

	svg.selectAll(".bar")
		.data(densityData)
		.enter().append("rect")
		.attr("class", "bar")
		.attr("x", d => x(d.startTime))
		.attr("y", d => yScale(d.topic) + (yScale.bandwidth() * 0.15))
		.attr("width", d => x(d.endTime) - x(d.startTime))
		.attr("height", yScale.bandwidth() * 0.7)
		.attr("fill", d => colorScale(d.density));
}

function drawBookmarks(llmTS) {
	const svg = d3.select(`#shared-axis-container svg`);
	svg.selectAll(".bookmark-marker").remove();

	const xScale = d3.scaleTime()
		.domain([new Date(globalState.globalStartTime), new Date(globalState.globalEndTime)])
		.range([0, globalState.dynamicWidth]);

	const bookmarkPath = "M22.5 4.5 L22.5 38.25 L11.25 22.5 L0 38.25 L0 4.5 Z";

	Object.entries(llmTS).forEach(([id, times]) => {
		times.forEach(timeStr => {
			const timestampMs = parseTimeToMillisRobust(timeStr);
			const xPosition = xScale(timestampMs);

			if (timestampMs >= new Date(globalState.globalStartTime).getTime()) {
				const bookmarkGroup = svg.append("g")
					.attr("transform", `translate(${xPosition + margin.left + margin.right}, 10)`)
					.attr("class", "bookmark-marker")
					.attr("id", `bookmark-${id}`)
					.on("mouseover", function () {
						d3.select(this).select("path").attr("fill", "#ff5722");
					})
					.on("mouseout", function () {
						d3.select(this).select("path").attr("fill", "#ff9800");
					})
					.on("click", function () {
						highlightAndScrollToInsight(id);
					});

				bookmarkGroup.append("path")
					.attr("d", bookmarkPath)
					.attr("fill", "#ff9800");

				bookmarkGroup.append("text")
					.attr("x", 11.25)
					.attr("y", 20)
					.attr("text-anchor", "middle")
					.attr("fill", "#000")
					.attr("font-size", "14px")
					.attr("font-weight", "bold")
					.text(id);
			}
		});
	});
}

function updateZoomLevelDisplay(zoomLevel) {
	const zoomDisplay = document.getElementById('zoom-level-display');
	zoomDisplay.textContent = `Zoom: ${zoomLevel}%`;
}

let currentZoomLevel = 100;
document.getElementById('spatial-view').addEventListener('wheel', function (event) {
	if (event.deltaY < 0) {
		currentZoomLevel = Math.min(currentZoomLevel + 10, 200);
	} else {
		currentZoomLevel = Math.max(currentZoomLevel - 10, 10);
	}
	updateZoomLevelDisplay(currentZoomLevel);
	event.preventDefault();
});

function setTimes(data) {
	// Handle empty data (realtime mode initialization)
	if (!data || data.length === 0) {
		const now = Date.now();
		globalState.globalStartTime = now;
		globalState.globalEndTime = now;
		globalState.intervalDuration = 1000;
		globalState.intervals = Array.from({
			length: globalState.bins + 1
		}, (v, i) => new Date(now + i * globalState.intervalDuration));
		globalState.lineTimeStamp1 = now;
		globalState.lineTimeStamp2 = now + 5000;
		return;
	}

	let timestamps = [];
	data.forEach(action => {
		if (action.Data && Array.isArray(action.Data)) {
			action.Data.forEach(subAction => {
				if (subAction.ActionInvokeTimestamp) {
					timestamps.push(parseTimeToMillisRobust(subAction.ActionInvokeTimestamp));
				}
			});
		}
	});
	timestamps.sort((a, b) => a - b);

	const globalStartTime = timestamps[0];
	const globalEndTime = timestamps[timestamps.length - 1];

	globalState.globalStartTime = globalStartTime;
	globalState.globalEndTime = globalEndTime;
	const totalTime = globalEndTime - globalStartTime;
	globalState.intervalDuration = totalTime / globalState.bins;
	globalState.intervals = Array.from({
		length: globalState.bins + 1
	}, (v, i) => new Date(globalStartTime + i * globalState.intervalDuration));
	globalState.lineTimeStamp1 = globalStartTime;
	globalState.lineTimeStamp2 = globalStartTime + 5000;
}

export function getGlobalState() {
	return globalState;
}

function createLines(timestamp1, timestamp2) {
	const heightFactor = 1.1;
	const y1 = 55;
	const alignX = 10;
	const svg = d3.select("#temporal-view");
	let height = parseInt(d3.select("#speech-plot-container").style("height")) * heightFactor;
	const dynamicWidth = globalState.dynamicWidth;

	let xPosition1 = Math.max(0, x(new Date(timestamp1))) + margin.left + alignX;
	let xPosition2 = Math.max(0, x(new Date(timestamp2))) + margin.left + alignX;

	let circle1 = svg.select('#time-indicator-circle1');
	circle1.attr('class', 'interactive');
	if (circle1.empty()) {
		circle1 = svg.append('circle')
			.attr('id', 'time-indicator-circle1')
			.attr('r', 5)
			.style('fill', '#9e9e9e');
	}

	let circle2 = svg.select('#time-indicator-circle2');
	circle2.attr('class', 'interactive');
	if (circle2.empty()) {
		circle2 = svg.append('circle')
			.attr('id', 'time-indicator-circle2')
			.attr('r', 5)
			.style('fill', '#9e9e9e');
	}

	function dragstarted(event, d) {
		d3.select(this).raise().classed("active", true);
	}

	function dragended(event, d) {
		d3.select(this).classed("active", false);
		generateHierToolBar();
		createPlotTemporal();
		plotUserSpecificBarChart();
		plotUserSpecificDurationBarChart();
	}

	var drag = d3.drag()
		.on("start", dragstarted)
		.on("drag", dragged)
		.on("end", dragended);

	let line1 = svg.select('#time-indicator-line1');
	if (line1.empty()) {
		line1 = svg.append('line').attr('id', 'time-indicator-line1');
	}

	line1.attr('x1', xPosition1)
		.attr('x2', xPosition1)
		.attr('y1', y1)
		.attr('y2', height)
		.style('stroke', '#9e9e9e')
		.style('stroke-width', '3')
		.style('opacity', 1)
		.attr('class', 'interactive')
		.call(drag);

	circle1.attr('cx', xPosition1)
		.attr('cy', y1)
		.call(drag);

	let line2 = svg.select('#time-indicator-line2');
	if (line2.empty()) {
		line2 = svg.append('line').attr('id', 'time-indicator-line2');
	}

	line2.attr('x1', xPosition2)
		.attr('x2', xPosition2)
		.attr('y1', y1)
		.attr('y2', height)
		.style('stroke', '#9e9e9e')
		.style('stroke-width', '3')
		.style('opacity', 1)
		.attr('class', 'interactive')
		.call(drag);

	circle2.attr('cx', xPosition2)
		.attr('cy', y1)
		.call(drag);

	circle1.call(drag);
	circle2.call(drag);
	updateRangeDisplay(timestamp1, timestamp2);
	updateXRSnapshot();
	for (let i = 0; i < numUsers; i++) {
		updateUserDevice(i);
		updateLeftControl(i);
		updateRightControl(i);
	}
	plotHeatmap();
	updatePointCloudBasedOnSelections();
	updateObjectsBasedOnSelections();
	updateMarkersBasedOnSelections();
	initializeShadedAreaDrag();
}

export function dragged(event, d) {
	const svgElement = document.querySelector("#temporal-view svg");
	const container = document.getElementById("temporal-view");
	const indicatorSvg = document.getElementById('indicator-svg');
	indicatorSvg.style.overflow = "visible";

	const height = parseInt(d3.select(svgElement).style("height")) - margin.top - margin.bottom;

	let newXPosition = event.x - margin.left;
	let newTimestamp = x.invert(newXPosition);

	const id = d3.select(this).attr('id');
	const isLine1 = id === 'time-indicator-line1' || id === 'time-indicator-circle1';
	const circleId = isLine1 ? '#time-indicator-circle1' : '#time-indicator-circle2';
	const otherCircleId = isLine1 ? '#time-indicator-circle2' : '#time-indicator-circle1';
	const lineId = isLine1 ? 'time-indicator-line1' : 'time-indicator-line2';
	const otherLineId = isLine1 ? 'time-indicator-line2' : 'time-indicator-line1';
	const timestampKey = isLine1 ? 'lineTimeStamp1' : 'lineTimeStamp2';
	let otherTimestamp = globalState[isLine1 ? 'lineTimeStamp2' : 'lineTimeStamp1'];
	const minDistanceMillis = 5000;

	if (newTimestamp > new Date(globalState.globalEndTime)) {
		newTimestamp = new Date(globalState.globalEndTime);
		newXPosition = x(newTimestamp);
	}
	if (isLine1) {
		newTimestamp = Math.min(newTimestamp, otherTimestamp - minDistanceMillis);
	} else {
		newTimestamp = Math.max(newTimestamp, otherTimestamp + minDistanceMillis);
	}
	if (isLine1) {
		globalState.lineTimeStamp1 = newTimestamp;
		globalState.lineTimeStamp2 = otherTimestamp;
		globalState.currentTimestamp = newTimestamp - globalState.globalStartTime;
		const binIndex = Math.floor(globalState.currentTimestamp / globalState.intervalDuration);
		globalState.startTimeStamp = globalState.globalStartTime + (binIndex * globalState.intervalDuration);
		globalState.endTimeStamp = globalState.startTimeStamp + globalState.intervalDuration;
	} else {
		globalState.lineTimeStamp2 = newTimestamp;
		globalState.lineTimeStamp1 = otherTimestamp;
	}
	newXPosition = x(new Date(newTimestamp));

	d3.select(this).attr('x1', newXPosition + margin.left).attr('x2', newXPosition + margin.left);
	d3.select(circleId).attr('cx', newXPosition + margin.left);

	createLines(globalState.lineTimeStamp1, globalState.lineTimeStamp2);
	const timeStamp1 = new Date(globalState.lineTimeStamp1);
	const timeStamp2 = globalState.lineTimeStamp2;
	updateRangeDisplay(timeStamp1, timeStamp2);
	updateXRSnapshot();
	initializeOrUpdateSpeechBox();
	updatePointCloudBasedOnSelections();
	updateObjectsBasedOnSelections();
	updateMarkersBasedOnSelections();
	for (let i = 0; i < numUsers; i++) {
		updateUserDevice(i);
		updateLeftControl(i);
		updateRightControl(i);
	}
	initializeShadedAreaDrag();
}

function updateNumUsers() {
	// In realtime mode, assume at least User0 even if data is empty
	if (REALTIME_CONFIG.enabled && globalState.finalData.length === 0) {
		uniqueUsers = new Set(['User0']);
		numUsers = 1;
		uniqueActions = [];
		dynamicColorMapping = generateDynamicColorMapping(uniqueUsers, uniqueActions);
		updateGlobalShow();
		return;
	}

	uniqueUsers = new Set(globalState.finalData.map(action => action.User));
	numUsers = uniqueUsers.size;
	uniqueActions = Array.from(new Set(globalState.finalData.map(action => action.Name)));
	dynamicColorMapping = generateDynamicColorMapping(uniqueUsers, uniqueActions);
	updateGlobalShow();
}

function updateGlobalShow() {
	// Preserve existing user check states, only add defaults for new users
	const oldShow = globalState.show || [];
	globalState.show = Array(numUsers + 1).fill(true);
	for (let i = 0; i < Math.min(oldShow.length, globalState.show.length); i++) {
		if (oldShow[i] !== undefined) {
			globalState.show[i] = oldShow[i];
		}
	}
}

function initHierToolBar() {
	const data = globalState.finalData;
	const uniqueActions = new Set();
	const toolbar = document.getElementById('hier-toolbar');
	toolbar.innerHTML = '';

	data.forEach(action => {
		action.Data.forEach(subAction => {
			uniqueActions.add(action.Name);
		});
	});

	uniqueActions.forEach(actionName => {
		createTopicItem(actionName, toolbar);
	});
}

function enableCheckboxes(actionNames, shouldCheck = true) {
	const allCheckboxes = document.querySelectorAll('.topic-checkbox');
	allCheckboxes.forEach(checkbox => {
		const actionName = checkbox.value;
		if (actionNames.includes(actionName)) {
			checkbox.disabled = false;
			checkbox.checked = shouldCheck;
		} else {
			checkbox.disabled = true;
			checkbox.checked = true;
		}
	});
}

function generateHierToolBar() {
	const data = getVisibleUsersData();
	if (!data || data.length === 0) {
		const toolbar = document.getElementById('hier-toolbar');
		if (toolbar) toolbar.innerHTML = '';
		return;
	}

	const uniqueActions = new Set();
	const toolbar = document.getElementById('hier-toolbar');

	const existingCheckboxes = new Set(
		Array.from(document.querySelectorAll('.topic-checkbox')).map(cb => cb.value)
	);

	data.forEach(action => {
		if (!action || !action.Data) return;

		action.Data.forEach(subAction => {
			if (!subAction || !subAction.ActionInvokeTimestamp) return;

			const actionStartTime = parseTimeToMillisRobust(subAction.ActionInvokeTimestamp);
			const actionEndTime = actionStartTime + parseDurationToMillis(action.Duration);
			if (actionEndTime >= globalState.lineTimeStamp1 && actionStartTime <= globalState.lineTimeStamp2) {
				uniqueActions.add(action.Name);

				if (!existingCheckboxes.has(action.Name)) {
					createTopicItem(action.Name, toolbar, true);
					existingCheckboxes.add(action.Name);
				}
			}
		});
	});

	enableCheckboxes([...uniqueActions], true);
}

function createTopicItem(actionName, toolbar, isEnabled = false) {
	const topicItem = document.createElement('li');
	const topicCheckbox = document.createElement('input');
	topicCheckbox.type = 'checkbox';
	topicCheckbox.id = `checkbox_broadtopic_${actionName.replace(/\s+/g, '_')}`;
	topicCheckbox.className = 'topic-checkbox';
	topicCheckbox.value = actionName;
	topicCheckbox.disabled = !isEnabled;

	// In realtime mode, newly created checkboxes are checked by default
	if (REALTIME_CONFIG.enabled && isEnabled) {
		topicCheckbox.checked = true;
	}

	const label = document.createElement('label');
	label.htmlFor = topicCheckbox.id;
	label.textContent = actionName;
	label.style.color = '#000';

	topicItem.appendChild(topicCheckbox);
	topicItem.appendChild(label);
	toolbar.appendChild(topicItem);

	topicCheckbox.addEventListener('change', function () {
		initializeOrUpdateSpeechBox();
		plotUserSpecificBarChart();
		plotUserSpecificDurationBarChart();
		updatePointCloudBasedOnSelections();
		updateObjectsBasedOnSelections();
		updateMarkersBasedOnSelections();
	});
}

function generateUserLegends() {
	const legendContainer = document.getElementById('user-toolbar-container');
	legendContainer.style.height = `${numUsers * globalState.obContext.length * 38}px`;

	const userArray = Array.from(uniqueUsers);

	userArray.forEach((userName, index) => {
		const i = index + 1;
		const userDiv = document.createElement('div');
		userDiv.classList.add('checkbox-container', 'collapsed');

		const userCheckboxLabel = document.createElement('label');
		userCheckboxLabel.classList.add('user-checkbox');

		const userCheckbox = document.createElement('input');
		userCheckbox.type = 'checkbox';
		userCheckbox.id = `toggle-user${i}`;
		userCheckbox.checked = (globalState.show[i] !== undefined) ? globalState.show[i] : true;
		userCheckboxLabel.appendChild(userCheckbox);

		const legendSquare = document.createElement('div');
		legendSquare.className = 'legend-square';
		legendSquare.style.backgroundColor = colorScale(userName);
		userCheckboxLabel.appendChild(legendSquare);

		userCheckboxLabel.appendChild(document.createTextNode(` ${userName}`));

		const nestedDiv = document.createElement('div');
		nestedDiv.classList.add('nested-checkboxes');

		globalState.obContext.forEach(context => {
			const contextLabel = document.createElement('label');
			const contextCheckbox = document.createElement('input');
			contextCheckbox.type = 'checkbox';
			contextCheckbox.id = `toggle-user${i}-${context}`;

			// Preserve previous check state
			if (globalState.viewProps[userName] && globalState.viewProps[userName][context] !== undefined) {
				contextCheckbox.checked = globalState.viewProps[userName][context];
			} else {
				// New user: in realtime mode, Context/Object/Tracemap checked by default
				if (REALTIME_CONFIG.enabled && (context === 'Context' || context === 'Object' || context === 'Tracemap')) {
					contextCheckbox.checked = true;
				}
			}

			contextLabel.appendChild(contextCheckbox);
			contextLabel.appendChild(document.createTextNode(` ${context}`));
			nestedDiv.appendChild(contextLabel);
			nestedDiv.appendChild(document.createElement('br'));

			contextCheckbox.addEventListener('change', function () {
				handleContextChange(context, userName, this.checked);
			});
		});

		const horizontalLine = document.createElement('div');
		horizontalLine.classList.add('horizontal-line');

		userDiv.appendChild(userCheckboxLabel);
		userDiv.appendChild(nestedDiv);
		userDiv.appendChild(horizontalLine);
		legendContainer.appendChild(userDiv);

		if (userCheckbox.checked) {
			nestedDiv.classList.add('show');
			userDiv.classList.add('expanded');
		} else {
			userDiv.classList.add('collapsed');
		}

		userCheckbox.addEventListener('change', function () {
			const hasVisibleUserID = Object.keys(globalState.show)
				.filter((userID) => globalState.show[userID])
				.map((userID) => Array.from(uniqueUsers)[userID - 1] || `User${userID}`);

			const baseHeightPerUser = 49;
			const obContextHeightPerItem = 28;

			if (this.checked) {
				nestedDiv.classList.add('show');
				userDiv.classList.remove('collapsed');
				userDiv.classList.add('expanded');
				const expandHeight = 58;
				legendContainer.style.height = `${expandHeight + (numUsers * baseHeightPerUser) + ((hasVisibleUserID.length - 1) * globalState.obContext.length * obContextHeightPerItem)}px`;
			} else {
				nestedDiv.classList.remove('show');
				userDiv.classList.remove('expanded');
				userDiv.classList.add('collapsed');
				legendContainer.style.height = `${(numUsers * baseHeightPerUser) + ((hasVisibleUserID.length - 2) * globalState.obContext.length * obContextHeightPerItem)}px`;
			}
		});
	});
}

function handleContextChange(context, userId, isChecked) {
	switch (context) {
		case 'Object':
			globalState.viewProps[userId]["Object"] = isChecked;
			updateObjectsBasedOnSelections();
			if (currentRealtimeHandModel && currentRealtimeHandModel.userData.user === userId) {
				currentRealtimeHandModel.visible = isChecked;
			}
			break;
		case 'Context':
			globalState.viewProps[userId]["Context"] = isChecked;
			updatePointCloudBasedOnSelections();
			if (currentRealtimeContextModel && currentRealtimeContextModel.userData.user === userId) {
				currentRealtimeContextModel.visible = isChecked;
			}
			break;
		case 'Tracemap':
			globalState.viewProps[userId]["Tracemap"] = isChecked;
			updateMarkersBasedOnSelections();
			break;
		default:
			break;
	}
}

function plotUserSpecificBarChart() {
	const plotBox = d3.select("#plot-box2").html("");
	const margin = { top: 30, right: 20, bottom: 70, left: 70 };
	const width = plotBox.node().getBoundingClientRect().width - margin.left - margin.right;
	const height = plotBox.node().getBoundingClientRect().height - margin.top - margin.bottom;

	if (width <= 0 || height <= 0) return;

	const svg = plotBox.append("svg")
		.attr("width", width + margin.left + margin.right)
		.attr("height", height + margin.top + margin.bottom)
		.append("g")
		.attr("transform", "translate(" + margin.left + "," + margin.top + ")");

	svg.append("text")
		.attr("x", width / 2)
		.attr("y", -4)
		.attr("text-anchor", "middle")
		.style("font-size", "16px")
		.style("font-family", "Lato")
		.style("font-weight", "bold")
		.text("User-Specific ActionReferentName");

	let allUsers = new Set();
	let userDataByActionReferentName = {};

	const selectedUsers = Object.keys(globalState.show)
		.filter(userID => globalState.show[userID])
		.map(userID => `User${userID}`);
	const selectedActions = getSelectedTopics();

	const filteredData = globalState.finalData.filter(action => {
		const isSelectedAction = selectedActions.includes(action.Name);
		const isSelectedUser = selectedUsers.includes(action.User);
		const hasTimeOverlap = action.Data.some(subAction => {
			const actionStartTime = parseTimeToMillisRobust(subAction.ActionInvokeTimestamp);
			const actionEndTime = actionStartTime + parseDurationToMillis(action.Duration);
			return actionEndTime >= globalState.lineTimeStamp1 && actionStartTime <= globalState.lineTimeStamp2;
		});
		return isSelectedAction && isSelectedUser && hasTimeOverlap;
	});

	filteredData.forEach(action => {
		const actorName = action.User;
		const referentData = action.Data.filter(ob => ob.ActionReferentName != null && ob.ActionReferentName !== undefined);

		referentData.forEach(ob => {
			let ActionReferentName = ob.ActionReferentName;
			if (typeof ActionReferentName === 'string' && ActionReferentName.trim() !== '') {
				try {
					ActionReferentName = ActionReferentName.split('_')[0];
					if (globalState.useCase == "sceneNavigation" || globalState.useCase == "videoScene") {
						if (action.ReferentType == "Physical") {
							ActionReferentName = ActionReferentName.split("(")[1].trim();
							ActionReferentName = ActionReferentName.split(",")[0].trim();
							ActionReferentName = ActionReferentName.replace(/'/g, "");
						}
					} else if (globalState.useCase == "maintenance") {
						const match = ActionReferentName.match(/"([^"]+)"|([a-zA-Z\s]+)/g);
						if (match) {
							ActionReferentName = match.join(' ').trim();
						}
					} else {
						ActionReferentName = ActionReferentName.split("(")[0].trim();
					}
					if (ActionReferentName && ActionReferentName !== '') {
						if (!userDataByActionReferentName[ActionReferentName]) {
							userDataByActionReferentName[ActionReferentName] = {};
						}
						userDataByActionReferentName[ActionReferentName][actorName] = (userDataByActionReferentName[ActionReferentName][actorName] || 0) + 1;
						allUsers.add(actorName);
					}
				} catch (e) {
					console.error(`[Chart] Error processing ReferentName: "${ob.ActionReferentName}"`, e);
				}
			}
		});
	});

	const users = Array.from(allUsers).sort((a, b) => a.localeCompare(b));
	const processedData = Object.entries(userDataByActionReferentName).map(([ActionReferentName, counts]) => ({
		ActionReferentName, ...counts
	}));

	const stack = d3.stack().keys(users).order(d3.stackOrderNone).offset(d3.stackOffsetNone);
	const stackedData = stack(processedData);
	const x = d3.scaleBand().rangeRound([0, width]).padding(0.8).domain(processedData.map(d => d.ActionReferentName));
	const y = d3.scaleLinear().domain([0, d3.max(stackedData, d => d3.max(d, d => d[1]))]).nice().range([height, 0]);

	svg.append("g")
		.selectAll("g")
		.data(stackedData)
		.enter().append("g")
		.attr("fill", d => colorScale(d.key))
		.selectAll("rect")
		.data(d => d)
		.enter().append("rect")
		.attr("x", d => x(d.data.ActionReferentName))
		.attr("y", d => y(d[1]))
		.attr("height", d => Math.max(0, y(d[0]) - y(d[1])))
		.attr("width", d => Math.min(x.bandwidth(), 40))
		.on("mouseover", function (event, d) {
			const userKey = d3.select(this.parentNode).datum().key;
			d3.select(this).transition().duration(100).attr("fill", d3.rgb(colorScale(userKey)).darker(2));
			tooltip.style("visibility", "visible")
				.text(`${userKey}: ${d.data[userKey]}`)
				.style("left", `${event.pageX + 5}px`)
				.style("top", `${event.pageY - 28}px`);
		})
		.on("mouseout", function (event, d) {
			const userKey = d3.select(this.parentNode).datum().key;
			d3.select(this).transition().duration(100).attr("fill", colorScale(userKey));
			tooltip.style("visibility", "hidden");
		});

	svg.append("g")
		.attr("class", "axis")
		.attr("transform", `translate(0,${height})`)
		.call(d3.axisBottom(x))
		.selectAll("text")
		.style("text-anchor", "end")
		.attr("dx", "0.2em")
		.attr("dy", ".25em")
		.attr("transform", "rotate(0)")
		.style("font-size", "1.2em")
		.each(function (d) {
			const element = d3.select(this);
			const words = d.split(" ");
			element.text("");
			words.forEach((word, i) => {
				element.append("tspan")
					.text(word)
					.attr("x", 0)
					.attr("dy", ".9em")
					.attr("dx", "-1em")
					.attr("text-anchor", "middle");
			});
		});

	svg.append("g")
		.call(d3.axisLeft(y).ticks(5))
		.selectAll(".tick text")
		.style("font-family", "Lato")
		.style("font-size", "1.2em");

	svg.append("text")
		.attr("transform", "rotate(-90)")
		.attr("y", 0 - margin.left)
		.attr("x", 0 - (height / 2))
		.attr("dy", "2em")
		.style("text-anchor", "middle")
		.text("Count")
		.style("font-size", "0.8em");

	const legend = svg.selectAll(".legend")
		.data(users)
		.enter().append("g")
		.attr("class", "legend")
		.attr("transform", (d, i) => `translate(0, ${i * 20})`);

	legend.append("rect")
		.attr("x", width - 18)
		.attr("width", 18)
		.attr("height", 18)
		.style("fill", colorScale);

	legend.append("text")
		.attr("x", width - 24)
		.attr("y", 9)
		.attr("dy", ".35em")
		.style("text-anchor", "end")
		.text(d => d)
		.style("font-size", "0.7em");

	const tooltip = d3.select("body").append("div")
		.attr("class", "d3-tooltip")
		.style("position", "absolute")
		.style("z-index", "1000")
		.style("text-align", "center")
		.style("width", "auto")
		.style("height", "auto")
		.style("padding", "8px")
		.style("font", "12px sans-serif")
		.style("background", "lightsteelblue")
		.style("border", "0px")
		.style("border-radius", "8px")
		.style("pointer-events", "none")
		.style("visibility", "hidden");
}

function plotUserSpecificDurationBarChart() {
	const plotBox = d3.select("#plot-box1").html("");
	const margin = { top: 30, right: 20, bottom: 60, left: 70 };
	const width = plotBox.node().getBoundingClientRect().width - margin.left - margin.right;
	const height = plotBox.node().getBoundingClientRect().height - margin.top - margin.bottom;

	if (width <= 0 || height <= 0) return;

	const svg = plotBox.append("svg")
		.attr("width", width + margin.left + margin.right)
		.attr("height", height + margin.top + margin.bottom)
		.append("g")
		.attr("transform", "translate(" + margin.left + "," + margin.top + ")");

	svg.append("text")
		.attr("x", width / 2)
		.attr("y", -4)
		.attr("text-anchor", "middle")
		.style("font-size", "16px")
		.style("font-family", "Lato")
		.style("font-weight", "bold")
		.text("User-Specific Action Durations");

	let allUsers = new Set();
	let userDurationByAction = {};

	const selectedUsers = Object.keys(globalState.show)
		.filter(userID => globalState.show[userID])
		.map(userID => `User${userID}`);
	const selectedActions = getSelectedTopics();

	globalState.finalData
		.filter(action => {
			const isSelectedAction = selectedActions.includes(action.Name);
			const isSelectedUser = selectedUsers.includes(action.User);
			const hasTimeOverlap = action.Data.some(subAction => {
				const actionStartTime = parseTimeToMillisRobust(subAction.ActionInvokeTimestamp);
				const actionEndTime = actionStartTime + parseDurationToMillis(action.Duration);
				return actionEndTime >= globalState.lineTimeStamp1 && actionStartTime <= globalState.lineTimeStamp2;
			});
			return isSelectedAction && isSelectedUser && hasTimeOverlap;
		})
		.forEach(action => {
			const actorName = action.User;
			const actionName = action.Name;
			const duration = parseDurationToMillis(action.Duration);
			if (!userDurationByAction[actionName]) {
				userDurationByAction[actionName] = {};
			}
			userDurationByAction[actionName][actorName] = (userDurationByAction[actionName][actorName] || 0) + duration;
			allUsers.add(actorName);
		});

	const users = Array.from(allUsers).sort((a, b) => a.localeCompare(b));
	const processedData = Object.entries(userDurationByAction).map(([actionName, durations]) => ({
		actionName, ...durations
	}));

	const maxDuration = d3.max(processedData, d => Math.max(...users.map(user => d[user] || 0)));
	let yLabel = "Total Duration (ms)";
	let yScaleFactor = 1;
	if (maxDuration > 9000) {
		yLabel = "Total Duration (s)";
		yScaleFactor = 1000;
	}

	const maxBandwidth = 120;
	const x0 = d3.scaleBand()
		.rangeRound([0, width])
		.paddingInner(0.1)
		.domain(processedData.map(d => d.actionName))
		.paddingOuter(0.1)
		.align(0.1);

	const x1 = d3.scaleBand()
		.padding(0.05)
		.domain(users)
		.rangeRound([0, Math.min(x0.bandwidth(), maxBandwidth)]);

	const y = d3.scaleLinear()
		.domain([0, maxDuration / yScaleFactor])
		.nice()
		.range([height, 0]);

	const action = svg.selectAll(".action")
		.data(processedData)
		.enter().append("g")
		.attr("class", "g")
		.attr("transform", d => `translate(${x0(d.actionName) + x0.bandwidth() / 2 - x1.bandwidth() * users.length / 2},0)`);

	action.selectAll("rect")
		.data(d => users.map(key => ({ key, value: d[key] || 0 })))
		.enter().append("rect")
		.attr("width", d => Math.min(x1.bandwidth(), 40))
		.attr("x", d => x1(d.key))
		.attr("y", d => y(d.value / yScaleFactor))
		.attr("height", d => Math.max(0, height - y(d.value / yScaleFactor)))
		.attr("fill", d => colorScale(d.key))
		.on("mouseover", function (event, d) {
			d3.select(this).transition().duration(100).attr("fill", d3.rgb(colorScale(d.key)).darker(2));
			tooltip.style("visibility", "visible")
				.text(`${d.key}: ${(d.value / yScaleFactor).toFixed(2)} ${yScaleFactor === 1000 ? 's' : 'ms'}`)
				.style("left", `${event.pageX + 5}px`)
				.style("top", `${event.pageY - 28}px`);
		})
		.on("mouseout", function (event, d) {
			d3.select(this).transition().duration(100).attr("fill", colorScale(d.key));
			tooltip.style("visibility", "hidden");
		});

	svg.append("g")
		.attr("class", "axis")
		.attr("transform", `translate(0,${height})`)
		.call(d3.axisBottom(x0))
		.selectAll("text")
		.style("text-anchor", "end")
		.attr("dx", "2em")
		.attr("dy", ".25em")
		.style("font-size", "1.2em")
		.each(function (d) {
			const element = d3.select(this);
			const words = d.split(" ");
			element.text("");
			words.forEach((word, i) => {
				element.append("tspan")
					.text(word)
					.attr("x", 0)
					.attr("dy", "1em")
					.attr("dx", "0.1em")
					.attr("text-anchor", "middle");
			});
		});

	svg.append("g")
		.call(d3.axisLeft(y).ticks(5))
		.selectAll(".tick text")
		.style("font-family", "Lato")
		.style("font-size", "1.2em");

	svg.append("text")
		.attr("transform", "rotate(-90)")
		.attr("y", 0 - margin.left)
		.attr("x", 0 - (height / 2))
		.attr("dy", "2em")
		.style("text-anchor", "middle")
		.text(yLabel)
		.style("font-size", "0.8em");

	const legend = svg.selectAll(".legend")
		.data(users)
		.enter().append("g")
		.attr("class", "legend")
		.attr("transform", (d, i) => `translate(0, ${i * 20})`);

	legend.append("rect")
		.attr("x", width - 18)
		.attr("width", 18)
		.attr("height", 18)
		.style("fill", colorScale);

	legend.append("text")
		.attr("x", width - 24)
		.attr("y", 9)
		.attr("dy", ".35em")
		.style("text-anchor", "end")
		.text(d => d)
		.style("font-size", "0.7em");

	const tooltip = d3.select("body").append("div")
		.attr("class", "d3-tooltip")
		.style("position", "absolute")
		.style("z-index", "1000")
		.style("text-align", "center")
		.style("width", "auto")
		.style("height", "auto")
		.style("padding", "8px")
		.style("font", "12px sans-serif")
		.style("background", "lightsteelblue")
		.style("border", "0px")
		.style("border-radius", "8px")
		.style("pointer-events", "none")
		.style("visibility", "hidden");
}

function plotLLMData() {
	d3.json(globalState.llmInsightPath).then(function (data) {
		globalState.llmInsightData = data;
		createAnalysisFilter(data);
		displayInsights(data);
		callDrawBookmarks(data);
	}).catch(function (error) {
		console.error('Error loading JSON data:', error);
	});
}

function callDrawBookmarks(llmInsightData) {
	if (!llmInsightData || typeof llmInsightData !== 'object') {
		return;
	}
	let entryTS = {};
	Object.keys(llmInsightData).forEach(key => {
		const insight = llmInsightData[key];
		if (insight && insight.timestamps && Array.isArray(insight.timestamps) && insight.timestamps.length !== 0) {
			entryTS[key] = insight.timestamps;
		}
	});
	drawBookmarks(entryTS);
}

function displayInsights(insightsData) {
	const insightsContainer = document.getElementById('insights-container');
	if (!insightsContainer) return;
	insightsContainer.innerHTML = '';
	if (!insightsData || typeof insightsData !== 'object') return;

	// Get currently visible users
	const visibleUsers = Object.keys(globalState.show)
		.filter(userID => globalState.show[userID])
		.map(userID => Array.from(uniqueUsers)[userID - 1] || `User${userID}`);

	Object.keys(insightsData).forEach(key => {
		const insight = insightsData[key];
		if (!insight) return;

		// Check if the insight's user is visible
		if (insight.seq !== undefined) {
			const event = globalState.finalData.find(e => e.seq === insight.seq);
			if (event) {
				const eventUser = event.User || (event.Data && event.Data[0] && event.Data[0].User);
				if (eventUser && !visibleUsers.includes(eventUser)) {
					return;
				}
			}
		}

		const insightBox = document.createElement('div');
		insightBox.className = 'insight-box';
		insightBox.id = `insight-${key}`;
		if (insight.status === 'pending') {
			insightBox.classList.add('insight-pending');
		} else {
			insightBox.classList.add('insight-completed');
		}

		const topicElement = document.createElement('h4');
		topicElement.className = 'insight-topic';

		const statusIndicator = document.createElement('span');
		statusIndicator.className = 'insight-status-indicator';
		statusIndicator.textContent = insight.status === 'pending' ? '...' : '✓';
		statusIndicator.title = insight.status === 'pending' ? 'Cloud optimization in progress...' : 'Completed';
		statusIndicator.style.marginRight = '8px';
		if (insight.status === 'pending') {
			statusIndicator.style.color = '#faad14';
			statusIndicator.style.animation = 'pulse 1.5s ease-in-out infinite';
		} else {
			statusIndicator.style.color = '#52c41a';
		}

		const keySpan = document.createElement('span');
		keySpan.className = 'insight-key';
		keySpan.textContent = `#${key}`;

		const topicText = document.createElement('span');
		topicText.textContent = insight.topic || `Insight ${key}`;

		topicElement.appendChild(statusIndicator);
		topicElement.appendChild(keySpan);
		topicElement.appendChild(topicText);

		const insightElement = document.createElement('p');
		insightElement.textContent = insight.insight;
		insightElement.className = 'insight-content';

		insightBox.appendChild(topicElement);
		insightBox.appendChild(insightElement);
		insightsContainer.appendChild(insightBox);

		insightBox.addEventListener('click', function () {
			const bookmark = d3.selectAll(`#bookmark-${key} path`);
			if (!bookmark.empty()) {
				const bookmarkDOM = document.getElementById(`bookmark-${key}`);
				bookmarkDOM.scrollIntoView({ behavior: 'smooth', block: 'center' });
				bookmark.transition().duration(500).attr("fill", "green")
					.transition().delay(1000).duration(500).attr("fill", "#ff9800");
			}
		});
	});
}

function createAnalysisFilter(insightsData) {
	const filterContainer = document.getElementById('analysis-filter-container');
	if (!filterContainer) return;
	filterContainer.innerHTML = '';
	if (!insightsData || typeof insightsData !== 'object') return;

	const analysisSet = new Set();
	Object.keys(insightsData).forEach(key => {
		const insight = insightsData[key];
		if (insight && insight.analyses && Array.isArray(insight.analyses)) {
			insight.analyses.forEach(analysis => analysisSet.add(analysis));
		}
	});

	analysisSet.forEach(analysis => {
		const filterTag = document.createElement('button');
		filterTag.textContent = analysis;
		filterTag.className = 'filter-tag active';
		filterTag.addEventListener('click', function () {
			this.classList.toggle('active');
			applyFilter(insightsData);
		});
		filterContainer.appendChild(filterTag);
	});
}

function applyFilter(insightsData) {
	const activeFilters = Array.from(document.querySelectorAll('.filter-tag.active')).map(tag => tag.textContent);
	if (activeFilters.length === 0) {
		displayInsights({});
		callDrawBookmarks({});
	} else {
		const filteredData = {};
		Object.keys(insightsData).forEach(key => {
			const insight = insightsData[key];
			if (activeFilters.some(filter => insight.analyses.includes(filter))) {
				filteredData[key] = insight;
			}
		});
		displayInsights(filteredData);
		callDrawBookmarks(filteredData);
	}
}

function highlightAndScrollToInsight(id) {
	const insightElement = document.getElementById(`insight-${id}`);
	if (insightElement) {
		insightElement.scrollIntoView({ behavior: 'smooth', block: 'center' });
		insightElement.classList.add('highlight-insight');
		setTimeout(() => {
			insightElement.classList.remove('highlight-insight');
		}, 2000);
	}
}

function getSelectedTopics() {
	const topicCheckboxes = document.querySelectorAll('.topic-checkbox:checked');
	let selectedActions = [];
	topicCheckboxes.forEach(checkbox => {
		if (!checkbox.disabled) {
			selectedActions.push(checkbox.value);
		}
	});
	return selectedActions;
}

// Legacy time parser (internal use only)
function parseTimeToMillisLegacy(customString) {
	let [dateStr, timeStr, milliStr] = customString.split('_');
	let year = parseInt(dateStr.slice(0, 2), 10) + 2000;
	let month = parseInt(dateStr.slice(2, 4), 10) - 1;
	let day = parseInt(dateStr.slice(4, 6), 10);
	let hours = parseInt(timeStr.slice(0, 2), 10) + 4;
	let minutes = parseInt(timeStr.slice(2, 4), 10);
	let seconds = parseInt(timeStr.slice(4, 6), 10);
	let milliseconds = parseInt(milliStr, 10);
	let date = new Date(Date.UTC(year, month, day, hours, minutes, seconds));
	let timeInMillis = date.getTime();
	return timeInMillis;
}

/**
 * Robust time parser supporting multiple formats:
 * Format 1: "251207_153605_116" (original UBR format)
 * Format 2: "15:36:05.116000" (frontend-friendly format)
 */
function parseTimeToMillisRobust(timeString) {
	if (!timeString) return 0;

	if (timeString.includes('_')) {
		// Original UBR format: "251207_153605_116"
		try {
			return parseTimeToMillisLegacy(timeString);
		} catch (e) {
			console.error(`Failed to parse UBR time: ${timeString}`, e);
			return 0;
		}
	} else if (timeString.includes(':')) {
		// Frontend-friendly format: "15:36:05.116000" or "15:36:05.116"
		try {
			const parts = timeString.split(':');
			if (parts.length !== 3) return 0;

			const hours = parseInt(parts[0], 10);
			const minutes = parseInt(parts[1], 10);
			const secParts = parts[2].split('.');
			const seconds = parseInt(secParts[0], 10);
			const milliseconds = secParts[1] ? parseInt(secParts[1].slice(0, 3), 10) : 0;

			const today = new Date();
			const date = new Date(
				today.getFullYear(),
				today.getMonth(),
				today.getDate(),
				hours,
				minutes,
				seconds,
				milliseconds
			);
			return date.getTime();
		} catch (e) {
			console.error(`Failed to parse frontend time: ${timeString}`, e);
			return 0;
		}
	}

	console.warn(`Unable to parse time format: ${timeString}`);
	return 0;
}

function updateSpatialView(nextTimestamp) {
	globalState.lineTimeStamp1 = nextTimestamp;
	globalState.lineTimeStamp2 = nextTimestamp + 10000;
	updatePointCloudBasedOnSelections();
	updateObjectsBasedOnSelections();
	updateMarkersBasedOnSelections();
}

function parseDurationToMillis(durationString) {
	if (!durationString) return 0;

	const durationStr = String(durationString);

	// -1 means continuous action
	if (durationStr === '-1') return 0;

	// Pure number (discrete action duration in seconds, e.g. 0.5)
	if (!durationStr.includes('_')) {
		const num = parseFloat(durationStr);
		return isNaN(num) ? 0 : num * 1000;
	}

	try {
		const parts = durationStr.split('_');
		if (!parts || parts.length < 3 || !parts[1] || !parts[2]) {
			console.warn('[parseDurationToMillis] Invalid format:', durationStr);
			return 0;
		}

		// parts[0]=days, parts[1]=HHMMSS, parts[2]=microseconds
		const hours = parseInt(parts[1].slice(0, 2), 10) || 0;
		const minutes = parseInt(parts[1].slice(2, 4), 10) || 0;
		const seconds = parseInt(parts[1].slice(4, 6), 10) || 0;
		const microseconds = parseInt(parts[2], 10) || 0;

		const totalMillis = (hours * 60 * 60 * 1000) + (minutes * 60 * 1000) + (seconds * 1000) + (microseconds / 1000);
		return totalMillis;
	} catch (error) {
		console.warn('[parseDurationToMillis] Parse failed:', durationStr, error);
		return 0;
	}
}

function initializeOrUpdateSpeechBox() {
	const selectedActions = getSelectedTopics();
	const data = globalState.finalData;
	const visibleUserIDs = Object.keys(globalState.show).filter(userID => globalState.show[userID]);

	const container = document.getElementById("speech-box");
	const hierToolbar = document.getElementById('hier-toolbar');
	let offsetHeight = hierToolbar.offsetHeight;
	container.style.top = `${offsetHeight}px`;

	let speechBoxesContainer = document.getElementById("speech-boxes-container");
	if (!speechBoxesContainer) {
		speechBoxesContainer = document.createElement('div');
		speechBoxesContainer.id = "speech-boxes-container";
		container.appendChild(speechBoxesContainer);
	} else {
		speechBoxesContainer.innerHTML = '';
	}

	const timeFormat = d3.timeFormat("%b %d %I:%M:%S %p");
	let rangeDisplay = document.querySelector('.time-range-display-speechbox');
	if (!rangeDisplay) {
		rangeDisplay = document.createElement('div');
		rangeDisplay.className = 'time-range-display-speechbox';
		container.appendChild(rangeDisplay);
	}
	rangeDisplay.innerHTML = `<strong>Selected Time Range: ${timeFormat(new Date(globalState.lineTimeStamp1))} - ${timeFormat(new Date(globalState.lineTimeStamp2))}</strong>`;

	let actionsToDisplay = data.filter(action => {
		const hasVisibleUserID = visibleUserIDs.some(userID => action.User.includes(userID));
		return hasVisibleUserID && selectedActions.includes(action.Name) && action.Data.some(subAction => {
			const actionStartTime = parseTimeToMillisRobust(subAction.ActionInvokeTimestamp);
			const actionEndTime = actionStartTime + parseDurationToMillis(action.Duration);
			return actionEndTime >= globalState.lineTimeStamp1 && actionStartTime <= globalState.lineTimeStamp2;
		});
	});

	const totalResults = actionsToDisplay.reduce((count, action) => count + action.Data.length, 0);
	let totalResultsDisplay = document.createElement('div');
	totalResultsDisplay.className = 'total-results-display';
	totalResultsDisplay.innerHTML = `<strong>About ${totalResults} results...</strong>`;
	speechBoxesContainer.appendChild(totalResultsDisplay);

	actionsToDisplay.forEach(action => {
		action.Data.forEach(subAction => {
			const speechBox = createSpeechBox(action, subAction);
			if (speechBox) {
				speechBoxesContainer.appendChild(speechBox);
			}
		});
	});
}

function updateXRSnapshot() {
	const container = document.getElementById('user-xr-snapshot');
	container.innerHTML = '';

	const titleElement = document.createElement('div');
	titleElement.style.textAlign = 'center';
	titleElement.id = 'imageTitle';
	container.appendChild(titleElement);

	const data = globalState.finalData;
	const visibleUserIDs = Object.keys(globalState.show).filter(userID => globalState.show[userID]);
	const selectedActions = getSelectedTopics();
	let imgPaths = [];

	let actionsToDisplay = data.filter(action => {
		const hasVisibleUserID = visibleUserIDs.some(userID => action.User.includes(userID));
		const filteredSubActions = action.Data.filter(subAction => {
			const actionStartTime = parseTimeToMillisRobust(subAction.ActionInvokeTimestamp);
			const actionEndTime = actionStartTime + parseDurationToMillis(action.Duration);
			const isInTimeRange = actionEndTime >= globalState.lineTimeStamp1 &&
				actionStartTime <= globalState.lineTimeStamp2;
			const containsPng = subAction.ActionReferentBody && subAction.ActionReferentBody.includes("png");
			if (isInTimeRange && containsPng) {
				imgPaths.push(subAction.ActionReferentBody);
			}
			return isInTimeRange && containsPng;
		});
		return hasVisibleUserID && selectedActions.includes(action.Name) && filteredSubActions.length > 0;
	});

	if (imgPaths.length >= 1) {
		container.style.display = 'block';
		const imageWrapper = document.createElement('div');
		imageWrapper.style.position = 'relative';
		imageWrapper.style.maxWidth = '100%';
		imageWrapper.style.maxHeight = '100%';
		imageWrapper.style.margin = 'auto';
		imageWrapper.style.overflow = 'hidden';
		imageWrapper.style.textAlign = 'center';

		imgPaths.forEach((imagePath, index) => {
			imagePath = globalState.objFilePath + '\\' + imagePath;
			const img = document.createElement('img');
			img.src = imagePath;
			img.alt = "Raw Capture Image";
			img.style.maxWidth = '100%';
			img.style.maxHeight = '100%';
			img.style.objectFit = 'contain';
			img.style.display = index === 0 ? 'block' : 'none';
			imageWrapper.appendChild(img);
		});

		container.appendChild(imageWrapper);
		addNavigationArrows(imageWrapper, imgPaths);
	} else {
		container.style.display = 'none';
	}
}

function addNavigationArrows(imageWrapper, imgPaths) {
	const prevArrow = document.createElement('button');
	prevArrow.innerHTML = '&#10094;';
	prevArrow.style.position = 'absolute';
	prevArrow.style.top = '50%';
	prevArrow.style.left = '5px';
	prevArrow.style.transform = 'translateY(-50%)';
	prevArrow.style.fontSize = '2em';
	prevArrow.style.background = 'rgba(0, 0, 0, 0.5)';
	prevArrow.style.color = 'white';
	prevArrow.style.border = 'none';
	prevArrow.style.padding = '1px';
	prevArrow.style.cursor = 'pointer';
	prevArrow.style.zIndex = '10';

	const nextArrow = document.createElement('button');
	nextArrow.innerHTML = '&#10095;';
	nextArrow.style.position = 'absolute';
	nextArrow.style.top = '50%';
	nextArrow.style.right = '5px';
	nextArrow.style.transform = 'translateY(-50%)';
	nextArrow.style.fontSize = '2em';
	nextArrow.style.background = 'rgba(0, 0, 0, 0.5)';
	nextArrow.style.color = 'white';
	nextArrow.style.border = 'none';
	nextArrow.style.padding = '5px';
	nextArrow.style.cursor = 'pointer';
	nextArrow.style.zIndex = '10';

	let currentIndex = 0;
	prevArrow.onclick = () => {
		currentIndex = (currentIndex - 1 + imgPaths.length) % imgPaths.length;
		changeImage(currentIndex, imageWrapper, imgPaths);
	};
	nextArrow.onclick = () => {
		currentIndex = (currentIndex + 1) % imgPaths.length;
		changeImage(currentIndex, imageWrapper, imgPaths);
	};

	imageWrapper.appendChild(prevArrow);
	imageWrapper.appendChild(nextArrow);
}

function changeImage(index, imageWrapper, imgPaths) {
	Array.from(imageWrapper.getElementsByTagName('img')).forEach((img, imgIndex) => {
		img.style.display = imgIndex === index ? 'block' : 'none';
	});
}

function formatLocation(locationString) {
	const location = parseLocation(locationString);
	if (!location) {
		return `<strong>Location:</strong> Invalid`;
	}
	const position = `${location.x.toFixed(3)}, ${location.y.toFixed(3)}, ${location.z.toFixed(3)}`;
	const orientation = `${location.pitch.toFixed(3)}, ${location.yaw.toFixed(3)}, ${location.roll.toFixed(3)}`;
	return `
		<strong>Position (X, Y, Z):</strong> ${position}<br>
		<strong>Orientation (Pitch, Yaw, Roll):</strong> ${orientation}
	`;
}

function createSpeechBox(action, subAction) {
	const speechBox = document.createElement('div');
	speechBox.className = 'speech-box';

	const titleContainer = document.createElement('div');
	titleContainer.style.display = 'flex';
	titleContainer.style.justifyContent = 'space-between';
	titleContainer.style.alignItems = 'center';
	titleContainer.style.marginBottom = '10px';

	const title = document.createElement('h4');
	title.textContent = `Action: ${action.Name}`;
	title.style.margin = '0';
	title.style.marginLeft = '10px';

	const userColor = colorScale(action.User);

	const userLabel = document.createElement('div');
	userLabel.textContent = action.User;
	userLabel.className = 'user-label';
	userLabel.style.backgroundColor = userColor;
	userLabel.style.padding = '5px 10px';
	userLabel.style.borderRadius = '5px';
	userLabel.style.color = 'white';
	userLabel.style.marginLeft = 'auto';

	titleContainer.appendChild(title);
	titleContainer.appendChild(userLabel);
	speechBox.appendChild(titleContainer);

	const locationString = subAction.ActionInvokeLocation;
	const formattedLocation = formatLocation(locationString);

	const details = document.createElement('div');
	const intentDiv = document.createElement('div');
	intentDiv.style.backgroundColor = 'rgba(235,235,235,1.0)';
	intentDiv.style.color = 'black';
	intentDiv.style.padding = '4px';
	intentDiv.style.borderRadius = '8px';
	intentDiv.style.fontSize = '1em';
	intentDiv.innerHTML = `<strong>Intent:</strong> ${action.Intent}`;

	let otherDetails;
	if (action.TriggerSource === "Audio") {
		otherDetails = `
			<strong>Timestamp:</strong> ${new Date(parseTimeToMillisRobust(subAction.ActionInvokeTimestamp)).toLocaleString()}<br>
			<strong>Duration:</strong> ${parseDurationToMillis(action.Duration)} ms<br>
			<strong>Trigger Source:</strong> ${action.TriggerSource}<br>
			<strong>Transcribed Text:</strong> ${subAction.ActionReferentBody}<br>
		`;
	} else {
		if (subAction.ActionReferentBody) {
			otherDetails = `
				<strong>Timestamp:</strong> ${new Date(parseTimeToMillisRobust(subAction.ActionInvokeTimestamp)).toLocaleString()}<br>
				<strong>Duration:</strong> ${parseDurationToMillis(action.Duration)} ms<br>
				<strong>Trigger Source:</strong> ${action.TriggerSource}<br>
				<strong>Referent Name:</strong> ${subAction.ActionReferentName || 'N/A'}<br>
			`;
		} else {
			otherDetails = `
				<strong>Timestamp:</strong> ${new Date(parseTimeToMillisRobust(subAction.ActionInvokeTimestamp)).toLocaleString()}<br>
				<strong>Duration:</strong> ${parseDurationToMillis(action.Duration)} ms<br>
				<strong>Trigger Source:</strong> ${action.TriggerSource}<br>
			`;
		}
	}
	details.appendChild(intentDiv);
	details.innerHTML += otherDetails;
	speechBox.appendChild(details);

	return speechBox;
}

function updateRangeDisplay(time1, time2) {
	const indicatorSVG = d3.select("#indicator-svg");
	indicatorSVG.selectAll("rect.shading").remove();
	const svg = d3.select("#temporal-view");

	const line1X = parseFloat(d3.select('#time-indicator-line1').attr('x1'));
	const line2X = parseFloat(d3.select('#time-indicator-line2').attr('x1'));
	const yStart = parseFloat(d3.select('#time-indicator-line1').attr('y1'));
	const yEnd = parseFloat(d3.select('#time-indicator-line1').attr('y2'));
	const height = Math.max(0, yEnd - yStart);
	const xStart = Math.min(line1X, line2X);
	const xEnd = Math.max(line1X, line2X);
	const shadingWidth = Math.max(0, xEnd - xStart);

	if (height <= 0 || shadingWidth <= 0 || isNaN(height) || isNaN(shadingWidth)) return;

	indicatorSVG.append("rect")
		.attr("class", "shading")
		.attr("x", xStart)
		.attr("y", yStart)
		.attr("width", shadingWidth)
		.attr("height", height)
		.attr("fill", "#9e9e9e")
		.attr("fill-opacity", 0.5);

	const timeFormat = d3.timeFormat("%b %d %I:%M:%S %p");
	const rangeDisplay = document.getElementById("range-display");
	if (rangeDisplay) {
		rangeDisplay.textContent = `Selected Time Range: ${timeFormat(new Date(time1))} - ${timeFormat(new Date(time2))}`;
	}
	initializeShadedAreaDrag();
}

function initializeShadedAreaDrag() {
	const indicatorSVG = d3.select("#indicator-svg");
	const shadedArea = indicatorSVG.select(".shading");
	let dragStartX = null;

	const dragstarted = (event) => {
		dragStartX = event.x;
	};

	const dragged = (event) => {
		const dx = event.x - dragStartX;
		const line1 = indicatorSVG.select("#time-indicator-line1");
		const line2 = indicatorSVG.select("#time-indicator-line2");
		const circle1 = indicatorSVG.select("#time-indicator-circle1");
		const circle2 = indicatorSVG.select("#time-indicator-circle2");

		let line1X = parseFloat(line1.attr("x1")) + dx;
		let line2X = parseFloat(line2.attr("x1")) + dx;

		line1.attr("x1", line1X).attr("x2", line1X);
		line2.attr("x1", line2X).attr("x2", line2X);
		circle1.attr("cx", line1X);
		circle2.attr("cx", line2X);

		updateShadedArea(line1X, line2X);

		const newLine1Timestamp = x.invert(line1X - buffer).getTime();
		const newLine2Timestamp = x.invert(line2X - buffer).getTime();
		globalState.lineTimeStamp1 = newLine1Timestamp;
		globalState.lineTimeStamp2 = newLine2Timestamp;

		updateRangeDisplay(newLine1Timestamp, newLine2Timestamp);

		for (let i = 0; i < numUsers; i++) {
			updateUserDevice(i);
			updateLeftControl(i);
			updateRightControl(i);
		}

		updatePointCloudBasedOnSelections();
		updateObjectsBasedOnSelections();
		updateMarkersBasedOnSelections();

		dragStartX = event.x;
	};

	const dragended = () => {
		generateHierToolBar();
		plotUserSpecificBarChart();
		plotUserSpecificDurationBarChart();
		updateObjectsBasedOnSelections();
		updatePointCloudBasedOnSelections();
		updateMarkersBasedOnSelections();
		initializeOrUpdateSpeechBox();
		updateXRSnapshot();
	};

	const drag = d3.drag()
		.on("start", dragstarted)
		.on("drag", dragged)
		.on("end", dragended);

	shadedArea.call(drag);
}

function updateShadedArea(line1X, line2X) {
	const indicatorSVG = d3.select("#indicator-svg");
	const shadedArea = indicatorSVG.select(".shading");
	const startX = Math.min(line1X, line2X);
	const endX = Math.max(line1X, line2X);
	shadedArea.attr("x", startX).attr("width", endX - startX);
}

function updateTimeDisplay(timestamp, startTime) {
	const elapsedMs = timestamp - startTime;
	const elapsedMinutes = Math.floor(elapsedMs / 60000);
	const elapsedSeconds = Math.floor((elapsedMs % 60000) / 1000);
	const milliseconds = Math.round((elapsedMs % 1000) / 10);
	const date = new Date(timestamp);
	const hours = date.getHours().toString().padStart(2, '0');
	const minutes = date.getMinutes().toString().padStart(2, '0');
	const seconds = date.getSeconds().toString().padStart(2, '0');
	const milliseconds2 = date.getMilliseconds().toString().padStart(3, '0');

	const timeDisplay = document.getElementById('timeDisplay');
	if (timeDisplay) {
		timeDisplay.textContent = `${hours}:${minutes}:${seconds}`;
	}
}

function createSharedAxis() {
	const { globalStartTime, globalEndTime, bins, unit } = globalState;
	const temporalViewContainer = d3.select("#temporal-view");
	const minWidth = document.getElementById('temporal-view').clientWidth - margin.right - margin.left;
	let sharedAxisContainer = temporalViewContainer.select("#shared-axis-container");
	if (sharedAxisContainer.empty()) {
		sharedAxisContainer = temporalViewContainer.append("div").attr("id", "shared-axis-container");
	}

	sharedAxisContainer.html("");
	const timeFormat = d3.timeFormat("%I:%M:%S");
	const totalDuration = globalEndTime - globalStartTime;
	let intervalSizeMillis;
	if (unit === 'minutes') {
		intervalSizeMillis = bins * 60 * 1000;
	} else {
		intervalSizeMillis = bins * 1000;
	}

	const totalDurationMillis = globalEndTime - globalStartTime;
	const numberOfIntervals = Math.ceil(totalDurationMillis / intervalSizeMillis);
	const widthPerInterval = 100;

	const intervalDuration = totalDuration * (bins / 100);
	globalState.dynamicWidth = numberOfIntervals * widthPerInterval;
	globalState.dynamicWidth = Math.max(globalState.dynamicWidth, minWidth);

	x = d3.scaleTime()
		.domain([new Date(globalStartTime), new Date(globalEndTime)])
		.range([0, globalState.dynamicWidth]);

	const xAxis = d3.axisTop(x)
		.ticks(d3.timeMillisecond.every(intervalSizeMillis))
		.tickFormat(timeFormat);

	const svg = sharedAxisContainer.append("svg")
		.attr("width", globalState.dynamicWidth + margin.left + margin.right)
		.attr("height", 50)
		.append("g")
		.attr("transform", `translate(${margin.left}, ${margin.top})`);

	svg.append("g")
		.attr("class", "x-axis")
		.call(xAxis)
		.selectAll("text")
		.style("font-size", "14px");
}

function onWindowResize() {
	const spatialView = document.getElementById('spatial-view');
	globalState.camera.aspect = spatialView.clientWidth / spatialView.clientHeight;
	globalState.camera.updateProjectionMatrix();
	globalState.renderer.setSize(spatialView.clientWidth, spatialView.clientHeight);
}

function initInteractionObjectViewer() {
	const container = document.getElementById('interaction-object-container');
	if (!container) return;
	const width = container.clientWidth;
	const height = container.clientHeight;
	globalState.interactionObjectScene = new THREE.Scene();
	globalState.interactionObjectScene.background = new THREE.Color(0xf8f9fa);
	globalState.interactionObjectCamera = new THREE.PerspectiveCamera(50, width / height, 0.1, 1000);
	globalState.interactionObjectCamera.position.set(0, 0, 3);
	globalState.interactionObjectRenderer = new THREE.WebGLRenderer({ antialias: true });
	globalState.interactionObjectRenderer.setSize(width, height);
	globalState.interactionObjectRenderer.setPixelRatio(window.devicePixelRatio);
	container.appendChild(globalState.interactionObjectRenderer.domElement);

	const ambientLight = new THREE.AmbientLight(0xffffff, 0.6);
	globalState.interactionObjectScene.add(ambientLight);
	const directionalLight = new THREE.DirectionalLight(0xffffff, 0.8);
	directionalLight.position.set(1, 1, 1);
	globalState.interactionObjectScene.add(directionalLight);

	const controls = new OrbitControls(globalState.interactionObjectCamera, globalState.interactionObjectRenderer.domElement);
	controls.enableDamping = true;
	controls.dampingFactor = 0.05;

	function animate() {
		requestAnimationFrame(animate);
		controls.update();
		globalState.interactionObjectRenderer.render(globalState.interactionObjectScene, globalState.interactionObjectCamera);
	}
	animate();
}

async function loadInteractionObject(referentName, user = 'User1') {
	if (!referentName) return;

	const cleanedName = referentName.trim();
	const glbPath = `obj/reference/${user}/${cleanedName}.glb`;

	try {
		const loader = new GLTFLoader();
		const gltf = await loader.loadAsync(glbPath);
		const model = gltf.scene;

		if (globalState.currentInteractionObject) {
			globalState.interactionObjectScene.remove(globalState.currentInteractionObject);
		}

		const box = new THREE.Box3().setFromObject(model);
		const center = box.getCenter(new THREE.Vector3());
		const size = box.getSize(new THREE.Vector3());

		const maxDim = Math.max(size.x, size.y, size.z);
		const scale = 2 / maxDim;
		model.scale.setScalar(scale);

		const scaledBox = new THREE.Box3().setFromObject(model);
		const scaledCenter = scaledBox.getCenter(new THREE.Vector3());

		model.position.x = -scaledCenter.x;
		model.position.y = -scaledBox.min.y;
		model.position.z = -scaledCenter.z;

		globalState.interactionObjectScene.add(model);
		globalState.currentInteractionObject = model;

		document.getElementById('interaction-object-name').textContent = `${user}: ${cleanedName}`;
	} catch (error) {
		console.error(`Failed to load GLB: ${user}/${cleanedName}`, error);
		document.getElementById('interaction-object-name').textContent = `${cleanedName} (not found)`;
	}
}

function updateActionReferenceForSelectedUsers() {
	const selectedUsers = Object.keys(globalState.show)
		.filter(userID => globalState.show[userID])
		.map(userID => `User${userID}`);

	if (selectedUsers.length === 0) {
		clearInteractionObjectViewer();
		return;
	}

	for (let i = globalState.finalData.length - 1; i >= 0; i--) {
		const event = globalState.finalData[i];
		if (selectedUsers.includes(event.User)) {
			const actionReferentName = event.Data?.[0]?.ActionReferentName;
			if (actionReferentName) {
				loadInteractionObject(actionReferentName, event.User);
				return;
			}
		}
	}
	clearInteractionObjectViewer();
}

function clearInteractionObjectViewer() {
	if (globalState.currentInteractionObject) {
		globalState.interactionObjectScene.remove(globalState.currentInteractionObject);
		globalState.currentInteractionObject.traverse((child) => {
			if (child.geometry) child.geometry.dispose();
			if (child.material) {
				if (Array.isArray(child.material)) {
					child.material.forEach(mat => mat.dispose());
				} else {
					child.material.dispose();
				}
			}
		});
		globalState.currentInteractionObject = null;
	}
	const labelElement = document.getElementById('interaction-object-name');
	if (labelElement) {
		labelElement.textContent = '';
	}
}

// ========== Realtime WebSocket ==========

function initRealtimeWebSocket() {
	if (!REALTIME_CONFIG.enabled) return;
	if (realtimeWebSocket && (realtimeWebSocket.readyState === WebSocket.OPEN ||
		realtimeWebSocket.readyState === WebSocket.CONNECTING)) {
		return;
	}

	console.log(`[Realtime] Connecting to: ${REALTIME_CONFIG.backendWS}`);
	realtimeWebSocket = new WebSocket(REALTIME_CONFIG.backendWS);

	realtimeWebSocket.onopen = () => {
		console.log('[Realtime] WebSocket connected');
	};

	realtimeWebSocket.onmessage = async (event) => {
		try {
			const message = JSON.parse(event.data);
			if (message.type === 'welcome') {
				console.log('[Realtime] Connected');
			} else if (message.type === 'new_glb') {
				await handleNewGLBNotification(message);
			} else if (message.type === 'new_log_event') {
				await handleNewLogEvent(message);
			} else if (message.type === 'insight_update') {
				await handleInsightUpdate(message);
			} else if (message.type === 'object_ready') {
				await handleObjectReady(message);
			}
		} catch (error) {
			console.error('[Realtime] Message processing failed:', error);
		}
	};

	realtimeWebSocket.onerror = (error) => {
		console.error('[Realtime] WebSocket error:', error);
	};

	realtimeWebSocket.onclose = (event) => {
		console.log('[Realtime] Disconnected, reconnecting in 3s...');
		setTimeout(initRealtimeWebSocket, REALTIME_CONFIG.reconnectInterval);
	};
}

async function handleNewGLBNotification(message) {
	const receiveTime = performance.now();
	const { data_type, glb_path, output_name, user } = message;
	const targetUser = user || "User1";

	try {
		// Dynamically add user if Context arrives before Log
		if (!uniqueUsers || !uniqueUsers.has(targetUser)) {
			if (!uniqueUsers) {
				uniqueUsers = new Set();
			}
			const oldNumUsers = numUsers;
			uniqueUsers.add(targetUser);
			numUsers = uniqueUsers.size;
			dynamicColorMapping = generateDynamicColorMapping(uniqueUsers, uniqueActions || []);
			updateGlobalShow();
			console.log(`[Realtime] Dynamic user added: ${targetUser}`);

			if (numUsers !== oldNumUsers) {
				initializeViewProps();
				const legendContainer = document.getElementById('user-toolbar-container');
				legendContainer.innerHTML = '';
				generateUserLegends();
				bindUserCheckboxListeners();
			}
		}

		const hasVisibleUserID = Object.keys(globalState.show)
			.filter((userID) => globalState.show[userID])
			.map((userID) => Array.from(uniqueUsers)[userID - 1] || `User${userID}`);

		const isUserVisible = hasVisibleUserID.includes(targetUser);

		let isContextEnabled = false;
		if (isUserVisible && globalState.viewProps[targetUser]) {
			if (data_type === "context") {
				isContextEnabled = globalState.viewProps[targetUser]["Context"] === true;
			} else if (data_type === "hand") {
				isContextEnabled = globalState.viewProps[targetUser]["Object"] === true;
			}
		}

		if (data_type === "context") {
			clearRealtimeContextModel();
		} else if (data_type === "hand") {
			clearRealtimeHandModel();
		}

		if (isContextEnabled) {
			const model = await loadAvatarModel(glb_path);
			if (model) {
				const loadedTime = performance.now();
				const latency = (loadedTime - receiveTime).toFixed(2);
				model.userData.dataType = data_type;
				model.userData.outputName = output_name;
				model.userData.loadTime = Date.now();
				model.userData.latency = latency;
				model.userData.user = targetUser;

				if (data_type === "context") {
					currentRealtimeContextModel = model;
				} else if (data_type === "hand") {
					currentRealtimeHandModel = model;
				}

				if (!globalState.loadedClouds[targetUser]) {
					globalState.loadedClouds[targetUser] = {};
				}
				globalState.loadedClouds[targetUser][output_name] = Promise.resolve(model);
			}
		}
	} catch (error) {
		console.error('[Realtime] GLB load failed:', error);
	}
}

function clearRealtimeContextModel() {
	const contextModelsToRemove = [];
	globalState.scene.traverse((obj) => {
		if (obj.userData && obj.userData.dataType === "context") {
			contextModelsToRemove.push(obj);
		}
	});

	contextModelsToRemove.forEach(model => {
		globalState.scene.remove(model);
		model.traverse((child) => {
			if (child.geometry) child.geometry.dispose();
			if (child.material) {
				if (Array.isArray(child.material)) {
					child.material.forEach(mat => mat.dispose());
				} else {
					child.material.dispose();
				}
			}
		});
	});

	Object.keys(globalState.loadedClouds).forEach(userId => {
		if (globalState.loadedClouds[userId]) {
			Object.keys(globalState.loadedClouds[userId]).forEach(key => {
				if (key.includes('context')) {
					const model = globalState.loadedClouds[userId][key];
					if (model && model.then) {
						model.then(m => {
							if (m && m.parent) {
								globalState.scene.remove(m);
							}
						});
					}
					delete globalState.loadedClouds[userId][key];
				}
			});
		}
	});

	currentRealtimeContextModel = null;
}

function clearRealtimeHandModel() {
	const handModelsToRemove = [];
	globalState.scene.traverse((obj) => {
		if (obj.userData && obj.userData.dataType === "hand") {
			handModelsToRemove.push(obj);
		}
	});

	handModelsToRemove.forEach(model => {
		globalState.scene.remove(model);
		model.traverse((child) => {
			if (child.geometry) child.geometry.dispose();
			if (child.material) {
				if (Array.isArray(child.material)) {
					child.material.forEach(mat => mat.dispose());
				} else {
					child.material.dispose();
				}
			}
		});
	});

	Object.keys(globalState.loadedClouds).forEach(userId => {
		if (globalState.loadedClouds[userId]) {
			Object.keys(globalState.loadedClouds[userId]).forEach(key => {
				if (key.includes('hand')) {
					const model = globalState.loadedClouds[userId][key];
					if (model && model.then) {
						model.then(m => {
							if (m && m.parent) {
								globalState.scene.remove(m);
							}
						});
					}
					delete globalState.loadedClouds[userId][key];
				}
			});
		}
	});

	currentRealtimeHandModel = null;
}

async function handleNewLogEvent(message) {
	try {
		const { seq, start_time, event, insight, insight_status } = message;
		if (!event || !event.Data || !Array.isArray(event.Data) || event.Data.length === 0) {
			console.error(`[Realtime] Invalid log data: seq=${seq}`);
			return;
		}
		if (realtimeLogEvents.length === 0) {
			clearRealtimeLogData();
		}
		event.insight_status = insight_status || 'completed';
		event.seq = seq;
		realtimeLogEvents.push(event);
		insertLogEventByStartTime(event, start_time);

		const oldUsers = new Set(uniqueUsers);
		const oldNumUsers = numUsers;
		updateNumUsers();
		const usersChanged = numUsers !== oldNumUsers ||
			!Array.from(uniqueUsers).every(user => oldUsers.has(user)) ||
			!Array.from(oldUsers).every(user => uniqueUsers.has(user));

		if (usersChanged) {
			initializeViewProps();
			const legendContainer = document.getElementById('user-toolbar-container');
			legendContainer.innerHTML = '';
			generateUserLegends();
			bindUserCheckboxListeners();
		}

		updateTimeRangeForNewLog(event);
		refreshAllVisualization();

		if (insight) {
			updateRealtimeInsight(seq, insight, insight_status);
		}

		const actionReferentName = event.Data[0]?.ActionReferentName;
		const eventUser = event.User || event.Data[0]?.User;
		const selectedUsers = Object.keys(globalState.show)
			.filter(userID => globalState.show[userID])
			.map(userID => `User${userID}`);

		if (actionReferentName && selectedUsers.includes(eventUser)) {
			await loadInteractionObject(actionReferentName, eventUser);
		}
	} catch (error) {
		console.error('[Realtime] Log processing failed:', error);
	}
}

function clearRealtimeLogData() {
	globalState.finalData = [];
	realtimeLogEvents = [];
	globalState.globalStartTime = 0;
	globalState.globalEndTime = 0;
	globalState.lineTimeStamp1 = 0;
	globalState.lineTimeStamp2 = 0;
	globalState.llmInsightData = {};
	const toolbar = document.getElementById('hier-toolbar');
	if (toolbar) {
		toolbar.innerHTML = '';
	}
	d3.select("#shared-axis-svg").selectAll("*").remove();
	d3.select("#plot-temporal-svg").selectAll("*").remove();
	d3.select("#plot-user-specific-bar-chart-svg").selectAll("*").remove();
	d3.select("#plot-user-specific-duration-bar-chart-svg").selectAll("*").remove();
	const insightContainer = document.getElementById('llm-insight-list');
	if (insightContainer) {
		insightContainer.innerHTML = '';
	}
}

function clearRealtimeTimeline() {
	console.log('[Realtime] Clearing timeline and all data (including GLB models)');
	clearRealtimeContextModel();
	clearRealtimeHandModel();
	if (globalState.loadedClouds["User1"]) {
		globalState.loadedClouds["User1"] = {};
	}
	clearRealtimeLogData();
}

function refreshAllVisualization() {
	try {
		if (!globalState.finalData || globalState.finalData.length === 0) return;
		createSharedAxis();
		createPlotTemporal();
		generateHierToolBar();
		initializeOrUpdateSpeechBox();
		plotUserSpecificBarChart();
		plotUserSpecificDurationBarChart();
		if (globalState.lineTimeStamp1 && globalState.lineTimeStamp2) {
			createLines(globalState.lineTimeStamp1, globalState.lineTimeStamp2);
		}
		updatePointCloudBasedOnSelections();
		updateObjectsBasedOnSelections();
		updateMarkersBasedOnSelections();
		if (globalState.llmInsightData) {
			callDrawBookmarks(globalState.llmInsightData);
		}
	} catch (error) {
		console.error('[Realtime] Refresh visualization failed:', error);
	}
}

function updateRealtimeInsight(seq, insightText, status) {
	if (!insightText) return;

	// Create incremental insight number
	const insightKeys = Object.keys(globalState.llmInsightData).map(k => parseInt(k));
	const maxInsightKey = insightKeys.length > 0 ? Math.max(...insightKeys) : 0;
	const insightNumber = maxInsightKey + 1;

	// Find corresponding event to get User and UserIntent
	const event = globalState.finalData.find(e => e.seq === seq);
	let insightTitle = `Insight #${insightNumber}`;
	if (event && event.User) {
		const userName = event.User;
		const userIntent = event.Intent || '';
		insightTitle = userIntent ? `${userName}: ${userIntent}` : `${userName}: NONE`;
	}

	globalState.llmInsightData[insightNumber] = {
		topic: insightTitle,
		insight: insightText,
		status: status || 'completed',
		seq: seq,
		analyses: [],
		timestamps: []
	};
	displayInsights(globalState.llmInsightData);
	callDrawBookmarks(globalState.llmInsightData);
	const statusEmoji = status === 'pending' ? '...' : 'OK';
	console.log(`[Realtime] ${statusEmoji} Insight updated: #${insightNumber} (seq=${seq}), status=${status}`);
}

async function handleInsightUpdate(message) {
	try {
		const { seq, insight, insight_status } = message;
		console.log(`[Realtime] Insight update received: seq=${seq}, cloud optimization complete`);

		const event = globalState.finalData.find(e => e.seq === seq);
		let insightTitle = `Insight #${seq}`;

		// Find the insightNumber corresponding to this seq
		let insightNumber = null;
		for (const [key, value] of Object.entries(globalState.llmInsightData)) {
			if (value.seq === seq) {
				insightNumber = parseInt(key);
				break;
			}
		}

		if (!insightNumber) {
			console.warn(`[Realtime] Insight not found for seq=${seq}`);
			return;
		}

		if (event && event.User) {
			const userName = event.User;
			const userIntent = event.Intent || '';
			insightTitle = userIntent ? `${userName}: ${userIntent}` : `${userName}`;
		}

		globalState.llmInsightData[insightNumber].insight = insight;
		globalState.llmInsightData[insightNumber].status = insight_status || 'completed';
		globalState.llmInsightData[insightNumber].topic = insightTitle;

		const eventIndex = globalState.finalData.findIndex(e => e.seq === seq);
		if (eventIndex !== -1) {
			globalState.finalData[eventIndex].insight_status = insight_status || 'completed';
			globalState.finalData[eventIndex].insight = insight;
		}

		displayInsights(globalState.llmInsightData);
		callDrawBookmarks(globalState.llmInsightData);

		setTimeout(() => {
			const insightBox = document.getElementById(`insight-${insightNumber}`);
			if (insightBox) {
				insightBox.classList.add('insight-updated');
				setTimeout(() => {
					insightBox.classList.remove('insight-updated');
				}, 2000);
			}
		}, 100);
	} catch (error) {
		console.error('[Realtime] Insight update failed:', error);
	}
}

async function handleObjectReady(message) {
	try {
		const { filename, object_name, user, glb_path } = message;
		console.log(`[Realtime] Object ready: ${object_name} (user=${user})`);

		// Attempt to load the referent GLB in the interaction viewer
		if (object_name && user) {
			await loadInteractionObject(object_name, user);
		}
	} catch (error) {
		console.error('[Realtime] Object processing failed:', error);
	}
}

function insertLogEventByStartTime(newEvent, startTimeStr) {
	try {
		const newStartTime = parseTimeToMillisRobust(startTimeStr);
		let left = 0;
		let right = globalState.finalData.length;
		while (left < right) {
			const mid = Math.floor((left + right) / 2);
			const midEvent = globalState.finalData[mid];
			let midStartTime;
			if (midEvent._startTimeSort) {
				midStartTime = parseTimeToMillisRobust(midEvent._startTimeSort);
			} else if (midEvent.Data && midEvent.Data.length > 0) {
				midStartTime = parseTimeToMillisRobust(midEvent.Data[0].ActionInvokeTimestamp);
			} else {
				midStartTime = 0;
			}
			if (newStartTime < midStartTime) {
				right = mid;
			} else {
				left = mid + 1;
			}
		}
		globalState.finalData.splice(left, 0, newEvent);
		console.log(`[Realtime] Event inserted at position ${left}/${globalState.finalData.length} | StartTime="${startTimeStr}"`);
	} catch (error) {
		console.error(`[Realtime] Insert event failed:`, error, `| startTime="${startTimeStr}"`);
		globalState.finalData.push(newEvent);
	}
}

function updateTimeRangeForNewLog(event) {
	try {
		if (!event || !event.Data || event.Data.length === 0) {
			console.warn('[Realtime] Invalid event data, skipping time range update');
			return;
		}
		const startTime = parseTimeToMillisRobust(event.Data[0]?.ActionInvokeTimestamp || event._startTimeSort);
		const durationMs = parseDurationToMillis(event.Duration);
		const endTime = startTime + durationMs;
		let timeRangeChanged = false;

		if (globalState.globalStartTime === 0 || startTime < globalState.globalStartTime) {
			globalState.globalStartTime = startTime;
			globalState.lineTimeStamp1 = startTime;
			timeRangeChanged = true;
		}
		if (endTime > globalState.globalEndTime) {
			globalState.globalEndTime = endTime;
			globalState.lineTimeStamp2 = endTime;
			timeRangeChanged = true;
		}
		if (timeRangeChanged) {
			console.log(`[Realtime] Time range updated: ${new Date(globalState.globalStartTime).toLocaleTimeString()} - ${new Date(globalState.globalEndTime).toLocaleTimeString()}`);
		}
	} catch (error) {
		console.error('[Realtime] Update time range failed:', error);
	}
}

function redrawTimelineWithNewEvent(newEvent) {
	appendEventToTimelineIncremental(newEvent);
	updateSharedAxisIfNeeded();
}

function appendEventToTimelineIncremental(event) {
	try {
		if (!event || !event.Data || !event.Data[0] || !event.User || !event.Name) {
			console.error('[Realtime] Incomplete event data:', event);
			showErrorPanel('appendEventToTimelineIncremental', 'Incomplete event data', JSON.stringify(event, null, 2));
			return;
		}
		const svg = d3.select("#speech-plot-container svg g");
		if (svg.empty()) {
			createPlotTemporal();
			return;
		}
		const startTime = parseTimeToMillisRobust(event.Data[0].ActionInvokeTimestamp || event._startTimeSort);
		const durationMillis = parseDurationToMillis(event.Duration || '0_000000_000');
		const endTime = startTime + durationMillis;

		const x = d3.scaleTime()
			.domain([globalState.globalStartTime, globalState.globalEndTime])
			.range([0, globalState.dynamicWidth]);

		const allActions = [...new Set(globalState.finalData.map(d => d.Name))];
		const yScale = d3.scaleBand()
			.domain(allActions)
			.rangeRound([0, 310])
			.padding(0.1);

		const colorScale = d3.scaleOrdinal(d3.schemeCategory10);
		const eventColor = colorScale(event.User);

		svg.append("rect")
			.attr("class", "bar realtime-event")
			.attr("x", x(startTime))
			.attr("y", yScale(event.Name) + (yScale.bandwidth() * 0.15))
			.attr("width", Math.max(1, x(endTime) - x(startTime)))
			.attr("height", yScale.bandwidth() * 0.7)
			.attr("fill", eventColor)
			.style("opacity", 0)
			.transition()
			.duration(300)
			.style("opacity", 0.8);
	} catch (error) {
		console.error('[Realtime] Incremental update failed:', error);
		showErrorPanel('appendEventToTimelineIncremental', error.message, error.stack);
	}
}

function updateSharedAxisIfNeeded() {
	const sharedAxisSvg = d3.select("#shared-axis");
	if (!sharedAxisSvg.empty()) {
		sharedAxisSvg.selectAll("*").remove();
		createSharedAxis();
	}
}

function updateViewsForNewLog() {
	// Sidebar update disabled to avoid flickering
}

async function initialize() {
	await initializeScene();
	const binsDropdown = document.getElementById('binsDropdown');
	globalState.bins = binsDropdown.value;
	createSharedAxis();
	createPlotTemporal();
	initHierToolBar();
	generateHierToolBar();
	createLines(globalState.lineTimeStamp1, globalState.lineTimeStamp2);
	document.querySelectorAll('.topic-checkbox').forEach(checkbox => {
		checkbox.checked = true;
		checkbox.dispatchEvent(new Event('change'));
	});
	initializeOrUpdateSpeechBox();
	if (!REALTIME_CONFIG.enabled && !logMode.infoVisCollab1) {
		plotLLMData();
	}
	updatePointCloudBasedOnSelections();
	updateObjectsBasedOnSelections();
	updateMarkersBasedOnSelections();
	plotUserSpecificBarChart();
	plotUserSpecificDurationBarChart();
	initInteractionObjectViewer();
	initRealtimeWebSocket();
}

let errorCount = 0;
const MAX_ERRORS_DISPLAY = 20;

function showErrorPanel(type, message, stack) {
	errorCount++;
	const panel = document.getElementById('error-panel');
	const errorList = document.getElementById('error-list');
	if (!panel || !errorList) {
		console.error('Error panel not found');
		return;
	}
	panel.style.display = 'block';
	const errorItem = document.createElement('div');
	errorItem.style.cssText = 'border-top:1px solid #fff; padding-top:8px; margin-top:8px;';
	errorItem.innerHTML = `
		<div style="color:#ff0; font-weight:bold;">#${errorCount} [${type}] ${new Date().toLocaleTimeString()}</div>
		<div style="color:#fff; margin:5px 0; word-break:break-all;">${message}</div>
		${stack ? `<details style="color:#ccc; font-size:10px;"><summary>Stack</summary><pre style="white-space:pre-wrap; margin:5px 0;">${stack}</pre></details>` : ''}
	`;
	errorList.insertBefore(errorItem, errorList.firstChild);
	while (errorList.children.length > MAX_ERRORS_DISPLAY) {
		errorList.removeChild(errorList.lastChild);
	}
}

window.addEventListener('error', function (event) {
	console.error('[Global Error]', event.error);
	showErrorPanel('JS Error', event.message || String(event.error), event.error?.stack);
	event.preventDefault();
});

window.addEventListener('unhandledrejection', function (event) {
	console.error('[Promise Rejection]', event.reason);
	showErrorPanel('Promise Rejection', String(event.reason), event.reason?.stack);
	event.preventDefault();
});

initialize();
globalState.camera.updateProjectionMatrix();
onWindowResize();

function animate() {
	requestAnimationFrame(animate);
	globalState.controls.update();
	globalState.renderer.render(globalState.scene, globalState.camera);
}
animate();
