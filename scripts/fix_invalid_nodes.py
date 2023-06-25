"""
Copyright 2023 Balázs Dukai, Ravi Peters

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

   http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
"""
import json
import os, sys
from pathlib import Path
import math

# TILESET_DIR = Path("/data/3DBAGv2/export/3dtiles/v202306xx/lod22")
# filename = TILESET_DIR / "tileset_og.json"
# output = TILESET_DIR / "tileset.json"

filename = sys.argv[1]
output = sys.argv[2]

TILESET_DIR = Path(filename).parent
print(TILESET_DIR)

def isInvalid(boundingVolume):
  x = boundingVolume["box"][0]
  y = boundingVolume["box"][1]
  z = boundingVolume["box"][2]
  t = 1e6
  for v in boundingVolume["box"]:
    if v > t: return True

def remove_invalid_leafs(node):
  if node.get("children"):
    # print("node has {} children".format(len(node["children"])))
    # print(node["boundingVolume"])
    new_children = []
    for child in node["children"]:
      if child.get("content"):
        # print("checking " + str(TILESET_DIR / child["content"]["uri"]))
        if(not os.path.exists(TILESET_DIR / (child["content"]["uri"]+".gz"))):
          print("Content uri does not exists: {}".format(child["content"]["uri"]+".gz"))
        elif(isInvalid(child["boundingVolume"])):
          print("Illegal bbox for {}:\n\t {}".format(child["content"]["uri"]+".gz", child["boundingVolume"]["box"]))
        else:
          new_children.append(child)
      else:
        new_children.append(child)
    if len(new_children) == 0:
      del node["children"]
    else:
      node["children"] = new_children
    # print(node["children"])
      for child in node["children"]:
        remove_invalid_leafs(child)

def get_aabb(boundingVolume):
  # xc, yc, zc, dx, 0, 0, 0, dy, 0, 0, 0, dz
  x_min = float(boundingVolume["box"][0]) - float(boundingVolume["box"][3])
  x_max = float(boundingVolume["box"][0]) + float(boundingVolume["box"][3])
  y_min = float(boundingVolume["box"][1]) - float(boundingVolume["box"][7])
  y_max = float(boundingVolume["box"][1]) + float(boundingVolume["box"][7])
  z_min = float(boundingVolume["box"][2]) - float(boundingVolume["box"][11])
  z_max = float(boundingVolume["box"][2]) + float(boundingVolume["box"][11])
  return (x_min, x_max, y_min, y_max, z_min, z_max)

def merge_aabb(aabb1, aabb2):
  x_min = min(aabb1[0], aabb2[0])
  x_max = max(aabb1[1], aabb2[1])
  y_min = min(aabb1[2], aabb2[2])
  y_max = max(aabb1[3], aabb2[3])
  z_min = min(aabb1[4], aabb2[4])
  z_max = max(aabb1[5], aabb2[5])
  return (x_min, x_max, y_min, y_max, z_min, z_max)

def set_from_aabb(aabb):
  # xc, yc, zc, dx, 0, 0, 0, dy, 0, 0, 0, dz
  dx = (aabb[1]-aabb[0])/2
  dy = (aabb[3]-aabb[2])/2
  dz = (aabb[5]-aabb[4])/2
  return (
    aabb[0] + dx,
    aabb[2] + dy,
    aabb[4] + dz,
    dx, 0, 0,
    0, dy, 0,
    0, 0, dz
  )

def compute_bounding_volume(node):
  
  aabb = get_aabb( node["children"][0]["boundingVolume"] )
  for child in node["children"][1:]:
    merge_aabb( aabb, get_aabb(child["boundingVolume"]) )

  node["boundingVolume"]["box"] = set_from_aabb(aabb)
  print(node["boundingVolume"]["box"])


def recompute_bounding_volumes(node):
  if node.get("content"):
    return node
  elif node.get("children"):
    # new_children = []
    for child in node["children"]:
      recompute_bounding_volumes(child)
    compute_bounding_volume(node)

with open(filename, "r") as in_file:
    tileset = json.load(in_file)

remove_invalid_leafs(tileset["root"])
recompute_bounding_volumes(tileset["root"])

with open(output, "w") as file:
    json.dump(tileset, file)
