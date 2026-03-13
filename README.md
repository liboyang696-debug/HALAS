# HALAS: On-Demand and AoI-Guided Explainability for Real-Time XR Analytics

This repository contains the implementation of **HALAS**, a hierarchical **cloud–edge–device** system for real-time and explainable XR behavior analytics.

HALAS moves XR explainability from traditional offline post-processing to **on-demand online inference**. The system prioritizes researcher-relevant behaviors on the XR device, performs low-latency behavior analysis on the edge server, and selectively escalates uncertain cases to cloud LLMs when deeper reasoning is needed.

For more details, please refer to our paper:

> **HALAS: On-Demand and AoI-Guided Explainability for Real-Time XR Analytics**

---

## Overview
- **AoI-aware capture on the XR device**  
  The device filters and prioritizes behaviors that are relevant to the researcher’s Analysis-of-Interest (AoI).

- **Edge-first behavior processing**  
  A behavior processor reconstructs UBR fragments and uses lightweight models to generate low-latency context descriptions and behavior insights.

- **Selective cloud escalation**  
  A token-confidence gate routes only uncertain cases to cloud LLMs for deeper semantic reasoning.

- **Visual analytics interface**  
  The interface presents point-cloud context, UBR statistics, and insights of UBR for real-time monitoring.
---

## Repository Structure

```text
.
## Repository Structure

```text
.
├── BehaviorProcessor/
├── UnityProject/
│   ├── AoIGuideXR/
│   └── ExplainableXR/
└── Visual Analytics Interface/
