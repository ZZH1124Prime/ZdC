#!/bin/sh
# 一键发布：先修改仓库根目录的 release.json，然后运行本文件。
exec python3 "$(dirname "$0")/release.py"
