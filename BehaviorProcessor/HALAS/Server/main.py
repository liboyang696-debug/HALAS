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
    """Migrate flat reference models to User1 and mirror them into the web assets."""
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
    frontend_notifier = None

    try:
        logger.info(f"Starting XR data server on ws://{config.WS_HOST}:{config.WS_PORT}")
        logger.info(
            "Model interfaces: tiny=%s (%s), image2text=%s (%s)",
            "enabled" if config.ENABLE_TINY_MODEL else "disabled",
            config.LLM_INSIGHT_API_URL,
            "enabled" if config.ENABLE_IMAGE2TEXT_MODEL else "disabled",
            config.IMAGE2TEXT_API_URL
        )
        logger.info(
            "VR baseline switch: default=%s, shared_config=%s",
            config.BASELINE_MODE,
            config.BASELINE_CONFIG_PATH or "config/vr_baseline_run.json"
        )

        frontend_notifier = FrontendNotifier(host=config.WS_HOST, port=config.FRONTEND_PORT)

        webapp_root = config.WEBAPP_ROOT
        realtime_output_dir = webapp_root / "obj" / "realtime"
        reference_dir = config.REFERENCE_DIR
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
    finally:
        logger.info("Shutting down XR data server...")
        if receiver:
            await receiver.stop_server()
        if frontend_notifier:
            await frontend_notifier.stop_server()
        if processor:
            await processor.shutdown()
        logger.info("Shutdown complete")


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        logger.info("Shutdown requested by keyboard interrupt")
