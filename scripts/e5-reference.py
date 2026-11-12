# /// script
# requires-python = ">=3.12,<3.14"
# dependencies = ["tokenizers==0.21.1", "onnxruntime==1.22.0", "numpy==2.2.6"]
# ///
"""Reference token ids and vectors for multilingual-e5-small (ADR 0008).

Weesite runs the model with Microsoft.ML.Tokenizers and ONNX Runtime in .NET; this computes
the same with Hugging Face's own tokenizer, for tests/Weesite.Search.Tests/Fixtures/e5-reference.json.
Usage: uv run scripts/e5-reference.py models/multilingual-e5-small > that file
(the directory needs tokenizer.json too, from the same commit as the Containerfile pins).
"""

import json
import sys

import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer

d = sys.argv[1]
tok = Tokenizer.from_file(f"{d}/tokenizer.json")
sess = ort.InferenceSession(f"{d}/model.onnx")
print(
    "inputs",
    [i.name for i in sess.get_inputs()],
    "outputs",
    [o.name for o in sess.get_outputs()],
    file=sys.stderr,
)
texts = [
    "query: how do I deploy to Azure Container Apps?",
    "passage: Kubernetesről és konténerekről szól ez a bejegyzés, árvíztűrő tükörfúrógép.",
    "passage: Hello, world! Ünnepi újdonságok: ő, ű, Ő, Ű — and .NET 11.",
]
out = []
for t in texts:
    enc = tok.encode(t)
    ids = np.array([enc.ids], dtype=np.int64)
    mask = np.ones_like(ids)
    feeds = {"input_ids": ids, "attention_mask": mask}
    if any(i.name == "token_type_ids" for i in sess.get_inputs()):
        feeds["token_type_ids"] = np.zeros_like(ids)
    hidden = sess.run(["last_hidden_state"], feeds)[0][0]
    vec = hidden.mean(axis=0)
    vec = vec / np.linalg.norm(vec)
    out.append({"text": t, "ids": enc.ids, "vector": [round(float(x), 6) for x in vec]})
json.dump(out, sys.stdout, ensure_ascii=False)
print()
