import asyncio
import logging
from xr_data_receiver import XRDataReceiver
from xr_data_processor import XRDataProcessor
from frontend_notifier import FrontendNotifier
import config

logging.basicConfig(
    level=getattr(logging, config.LOG_LEVEL),
    format=config.LOG_FORMAT
)
logger = logging.getLogger(__name__)

processor = None
receiver = None


def sync_reference_to_webapp(reference_dir, webapp_reference_dir):
    import shutil
    from pathlib import Path
    reference_dir = Path(reference_dir)
    webapp_reference_dir = Path(webapp_reference_dir)
    if not reference_dir.exists():
        return

    total_files = 0

    flat_glb_files = [f for f in reference_dir.glob("*.glb") if f.is_file()]
    if flat_glb_files:
        user1_dir = reference_dir / "User1"
        user1_dir.mkdir(parents=True, exist_ok=True)
        webapp_user1_dir = webapp_reference_dir / "User1"
        webapp_user1_dir.mkdir(parents=True, exist_ok=True)

        for glb_file in flat_glb_files:
            dest_file = user1_dir / glb_file.name
            shutil.move(str(glb_file), str(dest_file))
            shutil.copy2(str(dest_file), str(webapp_user1_dir / glb_file.name))
            total_files += 1

    for user_dir in reference_dir.iterdir():
        if user_dir.is_dir():
            glb_files = list(user_dir.glob("*.glb"))
            if glb_files:
                webapp_user_dir = webapp_reference_dir / user_dir.name
                webapp_user_dir.mkdir(parents=True, exist_ok=True)
                for glb_file in glb_files:
                    shutil.copy2(glb_file, webapp_user_dir / glb_file.name)
                    if user_dir.name != "User1" or glb_file not in flat_glb_files:
                        total_files += 1

    logger.info(f"Reference sync complete: {total_files} files")


async def main():
    global processor, receiver
    from pathlib import Path

    logger.info(f"Starting XR data server on ws://{config.WS_HOST}:{config.WS_PORT}")

    frontend_notifier = FrontendNotifier(host="0.0.0.0", port=8766)

    behavior_processor_root = Path(__file__).parent.parent.parent
    webapp_root = behavior_processor_root.parent / "webApp"
    realtime_output_dir = webapp_root / "obj" / "realtime"
    reference_dir = Path(__file__).parent.parent / "Reference"
    reference_webapp_dir = webapp_root / "obj" / "reference"
    reference_webapp_dir.mkdir(parents=True, exist_ok=True)

    sync_reference_to_webapp(reference_dir, reference_webapp_dir)

    receiver = XRDataReceiver(save_dir=str(realtime_output_dir / "received_data"))
    processor = XRDataProcessor(
        output_dir=str(realtime_output_dir),
        webapp_root=webapp_root,
        frontend_notifier=frontend_notifier,
        num_workers=2,
        reference_dir=str(reference_dir)
    )

    await frontend_notifier.start_server()

    await receiver.start_server(
        host=config.WS_HOST,
        port=config.WS_PORT,
        processor=processor
    )


if __name__ == "__main__":
    import sys
    import os
    import signal
    import atexit

    def hide_traceback(exctype, value, tb):
        if exctype is not KeyboardInterrupt:
            sys.__excepthook__(exctype, value, tb)
    sys.excepthook = hide_traceback

    def cleanup():
        if not processor:
            return
        try:
            processor.pointcloud_pool._processes.clear()
            processor.pointcloud_pool.shutdown(wait=False, cancel_futures=True)
        except Exception as e:
            logger.error(f"Cleanup error: {e}")

    def signal_handler(signum, frame):
        cleanup()
        os._exit(0)

    signal.signal(signal.SIGINT, signal_handler)
    atexit.register(cleanup)

    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
