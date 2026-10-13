#!/usr/bin/env bash
# server/toolset.mjs 是权威副本；plugin/toolset.mjs 是打包时随插件带走的那份。
# 插件安装时只复制 package.json 的 files 字段里声明的文件，跨目录引用会断，
# 所以需要这一份副本。改完 server/ 的版本后跑本脚本同步。
set -euo pipefail
cd "$(dirname "$0")"
cp server/toolset.mjs plugin/toolset.mjs
echo "已同步 server/toolset.mjs -> plugin/toolset.mjs"
