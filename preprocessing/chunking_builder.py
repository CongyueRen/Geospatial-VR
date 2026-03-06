import argparse
import json
import time
from pathlib import Path

import numpy as np

# ====== Config ======
NPZ_PATH = Path("data/TumTLS_v2_gaussians_demo_final.npz")
CHUNK_DIR = Path("data/chunks_TUMv2")
CHUNK_PREFIX = "chunk"
CHUNK_SIZE = np.array([10.0, 10.0, 10.0], dtype=np.float32)  # dx, dy, dz
EPS = 1e-5


def _log(msg: str):
    print(msg)


def _load_cov6(data) -> np.ndarray:
    if "cov6" in data:
        return data["cov6"].astype(np.float32, copy=False)

    if "cov0" in data and "cov1" in data:
        cov0 = data["cov0"].astype(np.float32, copy=False)  # (N,4): xx,xy,xz,yy
        cov1 = data["cov1"].astype(np.float32, copy=False)  # (N,4): yz,zz,0,0
        cov6 = np.empty((cov0.shape[0], 6), dtype=np.float32)
        cov6[:, 0] = cov0[:, 0]
        cov6[:, 1] = cov0[:, 1]
        cov6[:, 2] = cov0[:, 2]
        cov6[:, 3] = cov0[:, 3]
        cov6[:, 4] = cov1[:, 0]
        cov6[:, 5] = cov1[:, 1]
        return cov6

    raise KeyError("NPZ must contain 'cov6' or both 'cov0' and 'cov1'.")


def _chunk_ids_from_ijk(ijk: np.ndarray, ny: int, nz: int) -> np.ndarray:
    # Monotonic mapping preserving lexicographic (ix, iy, iz) order after sort.
    return (ijk[:, 0].astype(np.int64) * int(ny) + ijk[:, 1].astype(np.int64)) * int(nz) + ijk[:, 2].astype(np.int64)


def _decode_chunk_id(chunk_id: int, ny: int, nz: int) -> tuple[int, int, int]:
    ix = int(chunk_id // (ny * nz))
    rem = int(chunk_id % (ny * nz))
    iy = int(rem // nz)
    iz = int(rem % nz)
    return ix, iy, iz


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--npz", type=str, default=str(NPZ_PATH), help="Input NPZ path")
    parser.add_argument("--chunk_dir", type=str, default=str(CHUNK_DIR), help="Output chunk directory")
    parser.add_argument("--chunk_prefix", type=str, default=CHUNK_PREFIX, help="Chunk filename prefix")
    parser.add_argument("--chunk_size", type=float, nargs=3, default=CHUNK_SIZE.tolist(), help="Chunk size dx dy dz")
    args = parser.parse_args()

    npz_path = Path(args.npz)
    out_dir = Path(args.chunk_dir)
    chunk_prefix = str(args.chunk_prefix)
    chunk_size = np.asarray(args.chunk_size, dtype=np.float32)

    stage_times: dict[str, float] = {}
    t_start = time.perf_counter()

    t_stage = time.perf_counter()
    _log(f"[B] Loading NPZ: {npz_path}")
    if not npz_path.exists():
        raise FileNotFoundError(f"NPZ file not found: {npz_path}")

    data = np.load(npz_path)
    P = data["positions"].astype(np.float32, copy=False)  # (N,3)
    C = data["colors"].astype(np.float32, copy=False)     # (N,3)
    cov6 = _load_cov6(data)

    N = int(P.shape[0])
    _log(f"[B1] Loaded {N} Gaussians")
    stage_times["load_npz"] = time.perf_counter() - t_stage

    t_stage = time.perf_counter()
    xyz_min = P.min(axis=0)
    xyz_max = P.max(axis=0)

    _log("[B1] XYZ range:")
    _log(f"  X[{xyz_min[0]:.2f}, {xyz_max[0]:.2f}]")
    _log(f"  Y[{xyz_min[1]:.2f}, {xyz_max[1]:.2f}]")
    _log(f"  Z[{xyz_min[2]:.2f}, {xyz_max[2]:.2f}]")

    origin = xyz_min.copy()
    extent = xyz_max - xyz_min
    grid = np.ceil((extent + EPS) / chunk_size).astype(np.int64)
    nx, ny, nz = [int(v) for v in grid.tolist()]
    _log(f"[B1] Chunk grid (nx,ny,nz) = {nx} {ny} {nz}")
    stage_times["bbox_and_grid"] = time.perf_counter() - t_stage

    t_stage = time.perf_counter()
    rel = P - origin.reshape(1, 3)
    ijk = np.floor(rel / chunk_size.reshape(1, 3)).astype(np.int64)

    ijk[:, 0] = np.clip(ijk[:, 0], 0, nx - 1)
    ijk[:, 1] = np.clip(ijk[:, 1], 0, ny - 1)
    ijk[:, 2] = np.clip(ijk[:, 2], 0, nz - 1)

    chunk_ids = _chunk_ids_from_ijk(ijk, ny, nz)
    order = np.argsort(chunk_ids, kind="mergesort")
    sorted_ids = chunk_ids[order]

    unique_ids, start_idx, counts = np.unique(sorted_ids, return_index=True, return_counts=True)

    non_empty_chunks = int(unique_ids.shape[0])
    _log(f"[B2] Number of non-empty chunks: {non_empty_chunks}")

    preview_n = min(10, non_empty_chunks)
    for i in range(preview_n):
        ix, iy, iz = _decode_chunk_id(int(unique_ids[i]), ny, nz)
        _log(f"  chunk ({ix}, {iy}, {iz}) has {int(counts[i])} points")

    total_pts = int(counts.sum())
    _log(f"[B2] Total points over all chunks = {total_pts}")
    stage_times["build_chunk_groups"] = time.perf_counter() - t_stage

    t_stage = time.perf_counter()
    out_dir.mkdir(parents=True, exist_ok=True)
    chunk_meta: list[dict] = []

    _log(f"[B3] Writing chunk files to: {out_dir}")
    for i in range(non_empty_chunks):
        cid = int(unique_ids[i])
        s0 = int(start_idx[i])
        s1 = s0 + int(counts[i])

        idx_arr = order[s0:s1]

        P_chunk = P[idx_arr]
        C_chunk = C[idx_arr]
        cov6_chunk = cov6[idx_arr]

        mat = np.empty((P_chunk.shape[0], 12), dtype=np.float32)
        mat[:, 0:3] = P_chunk
        mat[:, 3:6] = C_chunk
        mat[:, 6:12] = cov6_chunk

        ix, iy, iz = _decode_chunk_id(cid, ny, nz)
        fname = f"{chunk_prefix}_{ix}_{iy}_{iz}.txt"
        fpath = out_dir / fname
        np.savetxt(fpath, mat, fmt="%.6f")

        bbox_min = P_chunk.min(axis=0)
        bbox_max = P_chunk.max(axis=0)
        center = 0.5 * (bbox_min + bbox_max)

        chunk_meta.append({
            "ijk": [ix, iy, iz],
            "filename": fname,
            "count": int(P_chunk.shape[0]),
            "bbox_min": bbox_min.tolist(),
            "bbox_max": bbox_max.tolist(),
            "center": center.tolist(),
        })

    _log(f"[B3] Wrote {len(chunk_meta)} chunk files.")
    stage_times["write_chunks"] = time.perf_counter() - t_stage

    t_stage = time.perf_counter()
    index = {
        "npz_source": str(npz_path),
        "origin": origin.tolist(),
        "chunk_size": chunk_size.tolist(),
        "grid_shape": [nx, ny, nz],
        "num_points": N,
        "num_chunks": len(chunk_meta),
        "chunks": chunk_meta,
    }

    index_path = out_dir / "chunks_index.json"
    with open(index_path, "w", encoding="utf-8") as f:
        json.dump(index, f, indent=2)

    _log(f"[B4] Wrote chunk index JSON to: {index_path}")
    stage_times["write_index_json"] = time.perf_counter() - t_stage

    elapsed = time.perf_counter() - t_start
    _log("[B] Stage runtime breakdown:")
    for key in ["load_npz", "bbox_and_grid", "build_chunk_groups", "write_chunks", "write_index_json"]:
        v = stage_times.get(key, 0.0)
        _log(f"  - {key}: {v:.2f} s ({(100.0 * v / max(elapsed, 1e-9)):.1f}%)")

    _log(f"[B] Total runtime: {elapsed:.2f} s ({elapsed / 60.0:.2f} min)")
    _log("[B4] Done.")


if __name__ == "__main__":
    main()
