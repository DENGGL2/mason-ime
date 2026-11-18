-- ============================================================
-- 云拼音（问云 / librime-cloud）
-- ------------------------------------------------------------
-- 用法：输入编码后按 Ctrl + T，才会联网查一次云端，取前 5 条
--      插到候选最前面。平时零联网、零延迟。
-- 云源：sougou（推荐，中文人名和新词质量最好）
--      想换 → 把下面的 "sougou" 改成 "baidu" 或 "google"
-- 依赖：小狼毫程序目录下的 simplehttp.dll + iconv.dll
--      （由「一键配置」脚本自动复制；缺了只是云输入不可用）
-- ============================================================
local ok, err = pcall(function()
  local provider = require("sougou")
  local cloud_pinyin = require("trigger")("Control+t", provider)
  cloud_pinyin_translator = cloud_pinyin.translator
  cloud_pinyin_processor  = cloud_pinyin.processor
end)

-- 用 pcall 保护：云插件出问题时只跳过云输入，
-- 不能连累白霜自带的日期 / 农历 / 计算器 / 纠错等 Lua 功能。
if not ok then
  log.error("云拼音加载失败，已跳过（不影响正常打字）: " .. tostring(err))
end
