import numpy as np
from PIL import Image
import io
import math
import trimesh
from pathlib import Path


def quaternion_to_rotation_matrix(qx, qy, qz, qw):
    norm = np.sqrt(qx*qx + qy*qy + qz*qz + qw*qw)
    qx, qy, qz, qw = qx/norm, qy/norm, qz/norm, qw/norm
    xx, yy, zz = qx*qx, qy*qy, qz*qz
    xy, xz, yz = qx*qy, qx*qz, qy*qz
    wx, wy, wz = qw*qx, qw*qy, qw*qz
    return np.array([
        [1-2*(yy+zz),   2*(xy - wz),   2*(xz + wy)],
        [  2*(xy + wz), 1-2*(xx+zz),   2*(yz - wx)],
        [  2*(xz - wy),   2*(yz + wx), 1-2*(xx+yy)]
    ], dtype=np.float64)


def load_depth01_from_bytes(depth_data: bytes, cx: int, cy: int) -> np.ndarray:
    depth_image = Image.open(io.BytesIO(depth_data))
    arr = np.array(depth_image)
    if arr.ndim == 3:
        arr = arr[..., 0]
    arr = arr.astype(np.float32)
    if depth_image.mode == 'L':
        arr /= 255.0
    elif depth_image.mode.startswith('I;16'):
        arr /= 65535.0
    return arr


def save_point_cloud_as_glb(points: np.ndarray, colors: np.ndarray, output_path: Path) -> bool:
    if points is None or points.size == 0:
        return False
    output_path.parent.mkdir(parents=True, exist_ok=True)
    cloud = trimesh.points.PointCloud(vertices=points, colors=colors)
    scene = trimesh.Scene([cloud])
    scene.export(str(output_path))
    return True


def reconstruct_point_cloud_task(
    color_data: bytes, depth_data: bytes, camera_json: dict,
    output_name: str, data_type: str, webapp_glb_dir: str, output_dir: str
) -> dict:
    try:
        screen_res = camera_json.get('screenResolution', {})
        screenW = screen_res.get('x', 1920)
        screenH = screen_res.get('y', 1080)
        far = camera_json.get('farClipPlane', 1000.0)
        fov_y = camera_json.get('fieldOfView', 60.0)
        position = camera_json.get('position', {})
        rotation = camera_json.get('rotation', {})

        px, py, pz = position.get('x', 0), position.get('y', 0), position.get('z', 0)
        qx, qy, qz, qw = rotation.get('x', 0), rotation.get('y', 0), rotation.get('z', 0), rotation.get('w', 1)

        R = quaternion_to_rotation_matrix(qx, qy, qz, qw)
        cam2world = np.eye(4, dtype=np.float64)
        cam2world[:3, :3] = R
        cam2world[:3, 3] = [px, py, pz]

        color_im = Image.open(io.BytesIO(color_data)).convert('RGB') if color_data else None
        color_w, color_h = color_im.size if color_im else (screenW, screenH)
        color_np = np.array(color_im) if color_im else None

        depth01_lr = load_depth01_from_bytes(depth_data, color_w // 2, color_h // 2)
        depth_hr = Image.fromarray(depth01_lr, mode='F').resize((color_w, color_h), Image.BILINEAR)
        depth_m = np.array(depth_hr, dtype=np.float32) * far

        aspect = screenW / screenH
        fov_x = 2 * math.degrees(math.atan(math.tan(math.radians(fov_y) / 2) * aspect))
        fy = (color_h * 0.5) / math.tan(math.radians(fov_y) / 2)
        fx = (color_w * 0.5) / math.tan(math.radians(fov_x) / 2)
        cx, cy = color_w * 0.5, color_h * 0.5

        sampling_stride = 2
        grid_xs, grid_ys = np.meshgrid(
            np.arange(0, color_w, sampling_stride, dtype=np.int32),
            np.arange(0, color_h, sampling_stride, dtype=np.int32)
        )
        Z = depth_m[grid_ys, grid_xs]
        valid = (Z > 0) & (Z < far)

        fail_result = {
            'success': False, 'glb_path': None, 'webapp_relative_path': None,
            'point_count': 0, 'output_name': output_name, 'data_type': data_type
        }

        if valid.sum() == 0:
            fail_result['error'] = 'Invalid depth data'
            return fail_result

        xv, yv, zv = grid_xs[valid].astype(np.float32), grid_ys[valid].astype(np.float32), Z[valid]

        dx = (xv + 0.5 - cx) * zv / fx
        dy = -(yv + 0.5 - cy) * zv / fy
        cam_pts = np.vstack([dx, dy, zv, np.ones_like(zv)])
        world = (cam2world @ cam_pts).T
        pts = (world[:, :3] / world[:, 3:4]) @ np.diag([-1.0, 1.0, 1.0])  # Unity to glTF

        cols = color_np[grid_ys[valid], grid_xs[valid], :] if color_np is not None \
            else np.full((len(pts), 3), 128, dtype=np.uint8)

        if len(pts) == 0:
            fail_result['error'] = 'Empty point cloud'
            return fail_result

        webapp_glb_dir_path = Path(webapp_glb_dir) if webapp_glb_dir else None
        if webapp_glb_dir_path:
            glb_path = webapp_glb_dir_path / f"{output_name}.glb"
            if not save_point_cloud_as_glb(pts, cols, glb_path):
                fail_result['error'] = 'GLB save failed'
                return fail_result
            webapp_relative_path = str(glb_path.relative_to(webapp_glb_dir_path.parent.parent.parent))
        else:
            glb_path = Path(output_dir) / "glb" / f"{output_name}.glb"
            if not save_point_cloud_as_glb(pts, cols, glb_path):
                fail_result['error'] = 'GLB save failed'
                return fail_result
            webapp_relative_path = None

        return {
            'success': True, 'glb_path': str(glb_path),
            'webapp_relative_path': webapp_relative_path,
            'point_count': len(pts),
            'output_name': output_name, 'data_type': data_type
        }

    except Exception as e:
        return {
            'success': False, 'error': str(e), 'glb_path': None,
            'webapp_relative_path': None, 'point_count': 0,
            'output_name': output_name, 'data_type': data_type
        }
