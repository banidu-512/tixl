---
id: ai-models-settings
title: AI Models Settings Page
scope: settings
tags: [ai, essential]
added: 2026-11-11
added-in-version: 4.4
prerequisites:
  - An empty project is open.
  - Network access is required only for the steps marked (network); everything else runs offline.
related-help:
  - ../.help/docs/using/AiModels.md
---

Verifies the *Settings → AI Models* page: the model store, the setup check with one-click
defaults, the per-family model browser and the engine runtime detection with path overrides.
Steps that download models are marked (network) in their titles.

## Step: Opening the AI Models page

**Action:**
Open the settings and select the **AI Models** category.

**Expected:**
- The page shows the sections **Model Store**, **Setup Check**, **Engine Runtimes** and **Model Browser**, in that order.
- No error is logged on first open, even with no models installed.

## Step: The store folder and Open folder

**Action:**
Read the **Store folder** path field, then press **Open folder**.

**Expected:**
- The field shows the model store path (`%LOCALAPPDATA%\TiXL\Models` by default on Windows).
- The file browser opens exactly at that folder, creating it if it did not exist yet.

## Step: Running the Setup Check

**Action:**
Press **Run check** under **Setup Check**.

**Expected:**
- After a short "Checking…" the result lists the model families (depth, diffusion, MediaPipe) with an **OK**, **Missing** or **Optional** status each.
- Missing models come with a readable reason, e.g. that a `[DepthAnything]` operator shows an error until a depth model is downloaded.
- The engine runtimes (CUDA, TensorRT, DirectML) are reported with readable reasons for what is not found.

## Step: Download Defaults (network)

**Action:**
With at least one family reporting a missing model, press **Download Defaults**.

**Expected:**
- Each default model downloads with a visible progress indicator, one after the other.
- When it finishes, the setup check result flips those families to **OK** without restarting the editor.
- The downloaded entries in the model browser are marked as installed by the one-click setup.

## Step: Family tabs in the model browser

**Action:**
Switch the model browser between **All**, **Depth Anything**, **Stream Diffusion** and **MediaPipe**.

**Expected:**
- **All** lists every entry grouped by family.
- **Depth Anything** lists only depth models (Depth Anything 3 in several sizes plus the V2 exports), **Stream Diffusion** only diffusion exports, **MediaPipe** only the tracking and segmentation models.
- Each entry shows what it is used for, its size and whether it is installed.

## Step: Installing one model (network)

**Action:**
Pick a small model that is not installed (e.g. a quantized Depth Anything 3 Small) and press **Download**.

**Expected:**
- The download streams in with a progress bar and can be cancelled while running.
- When it completes, the entry is marked **Installed** and the store's total size grows by roughly the stated model size.

## Step: Removing one model

**Action:**
Press **Remove** on the model installed in the previous step, confirm, then **Open folder** and check the store.

**Expected:**
- The entry is no longer marked as installed.
- The model's files are gone from the store folder; other installed models are untouched.

## Step: Engine runtime detection

**Action:**
Read the **Engine Runtimes** section, then press **Re-detect runtimes**.

**Expected:**
- CUDA, TensorRT and DirectML are each reported as found or not found, with a readable reason where they are not (e.g. the shipped DirectML build or a missing CUDA runtime).
- Re-detect re-probes without restarting the editor.

## Step: Runtime path override

**Action:**
Set the **TensorRT runtime folder** to an empty folder, press **Re-detect runtimes**, then clear the field and re-detect again.

**Expected:**
- With the override set, TensorRT is reported as not found and the message points at the folder that was checked.
- After clearing, the detection returns to what it was before (automatic sources like `TENSORRT_ROOT` and the standard installs).
