import logging
import os
import tempfile
import traceback
from pathlib import Path
from typing import Annotated, Any, Optional

from fastapi import FastAPI, File, Form, UploadFile
from fastapi.responses import JSONResponse

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

app = FastAPI(title="PaddleOCR Server")

_LANG_MAP = {
    "ja": "japan",
    "zh": "ch",
    "zh-hans": "ch",
    "zh-hant": "chinese_cht",
    "ko": "korean",
}

_VL_MODELS = {"PaddleOCR-VL-1.5", "PaddleOCR-VL-2.0"}
_STRUCTURE_MODELS = {"PP-StructureV3"}

_readers: dict[tuple[str, str], Any] = {}


def _to_paddle_lang(bcp47: str) -> str:
    return _LANG_MAP.get(bcp47.lower(), bcp47)


def _device() -> str:
    return "gpu" if os.environ.get("PADDLEOCR_USE_GPU", "false").lower() == "true" else "cpu"


def _is_vl(model_name: str) -> bool:
    return model_name in _VL_MODELS


def _is_structure(model_name: str) -> bool:
    return model_name in _STRUCTURE_MODELS


def _get_reader(lang_code: str, model_name: str):
    key = (lang_code, model_name)
    if key in _readers:
        return _readers[key]

    device = _device()
    # Disable oneDNN for CPU inference: PaddlePaddle 3.x PIR executor has a known incompatibility with
    # the oneDNN backend in ConvertPirAttribute2RuntimeAttribute (pir::ArrayAttribute<DoubleAttribute>).
    # Global FLAGS_use_mkldnn is not honored by the PaddleX inference Config, so disable it explicitly in
    # the constructor.
    cpu_kwargs = {"enable_mkldnn": False} if device == "cpu" else {}
    if _is_structure(model_name):
        from paddleocr import PPStructureV3
        _readers[key] = PPStructureV3(lang=lang_code, device=device, **cpu_kwargs)
    elif _is_vl(model_name):
        # PaddleOCR-VL pipeline (GPU recommended). Markdown native.
        from paddleocr import PaddleOCRVL
        _readers[key] = PaddleOCRVL(device=device, **cpu_kwargs)
    else:
        # Legacy line-level OCR (PP-OCRv4 etc.). No native Markdown.
        from paddleocr import PaddleOCR
        _readers[key] = PaddleOCR(
            ocr_version=model_name,
            lang=lang_code,
            use_textline_orientation=True,
            device=device,
            **cpu_kwargs,
        )
    return _readers[key]


def _markdown_text(md_info: Any) -> str:
    if isinstance(md_info, dict):
        return md_info.get("markdown_texts") or md_info.get("markdown") or md_info.get("md") or ""
    if md_info is None:
        return ""
    return str(md_info)


def _process_structure(file_path: str, reader, max_pages: Optional[int]) -> tuple[list[dict], str, str, int]:
    """PP-StructureV3 pipeline: file path in, page-level Markdown out."""
    page_blocks: list[dict] = []
    page_markdowns: list[str] = []
    page_count = 0
    for page_num, res in enumerate(reader.predict(file_path), start=1):
        if max_pages is not None and page_num > max_pages:
            break
        page_count += 1
        md_text = _markdown_text(getattr(res, "markdown", None))
        page_markdowns.append(md_text)
        page_blocks.append({
            "text": md_text,
            "confidence": 1.0,
            "page": page_num,
            "bbox": [0, 0, 0, 0],
        })

    # Multi-page: separate with horizontal rule to preserve page boundary; same as VL branch.
    markdown_payload = "\n\n---\n\n".join(p for p in page_markdowns if p)
    plain_text = "\n\n".join(page_markdowns)
    return page_blocks, plain_text, markdown_payload, page_count


def _process_vl(file_path: str, reader, max_pages: Optional[int]) -> tuple[list[dict], str, str, int]:
    """PaddleOCR-VL pipeline: file path in, page-level Markdown out (GPU)."""
    page_blocks: list[dict] = []
    page_markdowns: list[str] = []
    page_count = 0
    for page_num, res in enumerate(reader.predict(file_path), start=1):
        if max_pages is not None and page_num > max_pages:
            break
        page_count += 1
        md_text = _markdown_text(getattr(res, "markdown", None))
        page_markdowns.append(md_text)
        page_blocks.append({
            "text": md_text,
            "confidence": 1.0,
            "page": page_num,
            "bbox": [0, 0, 0, 0],
        })

    markdown_payload = "\n\n---\n\n".join(p for p in page_markdowns if p)
    plain_text = "\n\n".join(page_markdowns)
    return page_blocks, plain_text, markdown_payload, page_count


def _process_pp_ocr(
    file_path: str,
    reader,
    include_bboxes: bool,
    max_pages: Optional[int],
) -> tuple[list[dict], str, None, int]:
    """Legacy line-level OCR (PP-OCRv4 etc.). Returns blocks; markdown is None."""
    blocks: list[dict] = []
    texts: list[str] = []
    page_count = 0
    for page_num, res in enumerate(reader.predict(file_path), start=1):
        if max_pages is not None and page_num > max_pages:
            break
        page_count += 1
        # `res.json` returns {"res": {...}} in 3.x.
        payload = res.json
        page_data = payload.get("res", payload) if isinstance(payload, dict) else {}
        rec_texts = page_data.get("rec_texts") or []
        rec_scores = page_data.get("rec_scores") or []
        rec_boxes = page_data.get("rec_boxes") or []

        for i, text in enumerate(rec_texts):
            confidence = float(rec_scores[i]) if i < len(rec_scores) else 0.0
            block: dict = {"text": text, "confidence": confidence, "page": page_num}
            if include_bboxes and i < len(rec_boxes):
                box = rec_boxes[i]  # [x_min, y_min, x_max, y_max]
                x_min, y_min, x_max, y_max = (float(v) for v in box[:4])
                block["bbox"] = [x_min, y_min, x_max - x_min, y_max - y_min]
            else:
                block["bbox"] = [0, 0, 0, 0]
            blocks.append(block)
            texts.append(text)

    return blocks, "\n".join(texts), None, page_count


def _suffix_for(filename: str, content_type: Optional[str]) -> str:
    suffix = Path(filename).suffix.lower() if filename else ""
    if suffix:
        return suffix
    if content_type == "application/pdf":
        return ".pdf"
    return ".png"


def _paddle_version() -> Optional[str]:
    try:
        import paddleocr
        return getattr(paddleocr, "__version__", None)
    except Exception:
        return None


@app.post("/ocr")
async def ocr(
    file: Annotated[UploadFile, File()],
    languages: Annotated[str, Form()] = "ja,en",
    model_name: Annotated[str, Form()] = "PP-StructureV3",
    include_bboxes: Annotated[str, Form()] = "false",
    max_pages: Annotated[Optional[int], Form()] = None,
):
    lang_list = [l.strip() for l in languages.split(",") if l.strip()]
    lang_code = _to_paddle_lang(lang_list[0]) if lang_list else "japan"
    include_bbox = include_bboxes.lower() == "true"

    file_bytes = await file.read()
    suffix = _suffix_for(file.filename or "", file.content_type)

    # PP-StructureV3 / VL pipelines accept file paths directly (PDF or image).
    # Stage to a temp file so all paths are uniform.
    with tempfile.NamedTemporaryFile(suffix=suffix, delete=False) as tmp:
        tmp.write(file_bytes)
        tmp_path = tmp.name

    try:
        reader = _get_reader(lang_code, model_name)
        if _is_structure(model_name):
            blocks, plain_text, markdown_payload, page_count = _process_structure(tmp_path, reader, max_pages)
        elif _is_vl(model_name):
            blocks, plain_text, markdown_payload, page_count = _process_vl(tmp_path, reader, max_pages)
        else:
            blocks, plain_text, markdown_payload, page_count = _process_pp_ocr(
                tmp_path, reader, include_bbox, max_pages
            )
    except Exception as exc:
        logger.exception("OCR processing failed for model=%s lang=%s", model_name, lang_code)
        return JSONResponse(
            status_code=500,
            content={"error": str(exc), "detail": traceback.format_exc()},
        )
    finally:
        try:
            os.unlink(tmp_path)
        except OSError:
            pass

    confidences = [b["confidence"] for b in blocks]
    avg_confidence = sum(confidences) / len(confidences) if confidences else 0.0

    return JSONResponse({
        "raw_text": plain_text,
        "markdown": markdown_payload,
        "blocks": blocks,
        "confidence": avg_confidence,
        "detected_language": lang_list[0] if lang_list else None,
        "page_count": page_count,
        "provider_name": "PaddleOCR",
        "provider_model": model_name,
        "provider_version": _paddle_version(),
    })


@app.get("/health")
def health():
    return {"status": "ok"}
