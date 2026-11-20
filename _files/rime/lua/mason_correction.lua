-- One adjacent QWERTY key substitution, decoded by the existing Rime dictionary.
-- No context.input rewriting, network calls, external processes or typo learning.
local M = {}
local marker = utf8.char(0x2063)
local syllables
local neighbors = {}
local positions = {}
for row, keys in ipairs({'qwertyuiop', 'asdfghjkl', 'zxcvbnm'}) do
  local offset = ({0, .25, .75})[row]
  for column = 1, #keys do positions[keys:sub(column, column)] = {column - 1 + offset, row - 1} end
end
for key, point in pairs(positions) do
  neighbors[key] = {}
  for other, next_point in pairs(positions) do
    local dx, dy = point[1] - next_point[1], point[2] - next_point[2]
    local distance = math.sqrt(dx * dx + dy * dy)
    if other ~= key and distance <= 1.3 then
      neighbors[key][#neighbors[key] + 1] = {letter=other, distance=distance}
    end
  end
  table.sort(neighbors[key], function(a, b)
    if a.distance == b.distance then return a.letter < b.letter end
    return a.distance < b.distance
  end)
end

local function load_syllables()
  if syllables then return true end
  local file = io.open(rime_api.get_user_data_dir() .. '/cn_dicts/8105.dict.yaml', 'rb')
  if not file then return false end
  local values = {}
  for line in file:lines() do
    local code = line:match('^[^\t]+\t([a-z]+)')
    if code and #code <= 6 then values[code] = true end
  end
  file:close()
  if not values.ni or not values.hao then return false end
  syllables = values
  return true
end

local function legal_part(input)
  local reachable = {[0]=true}
  for finish = 1, #input do
    for length = 1, math.min(6, finish) do
      if reachable[finish - length] and syllables[input:sub(finish - length + 1, finish)] then
        reachable[finish] = true; break
      end
    end
  end
  return reachable[#input] == true
end
local function legal(input)
  if input:sub(1, 1) == "'" or input:sub(-1) == "'" or input:find("''", 1, true) then return false end
  for part in input:gmatch("[^']+") do if not legal_part(part) then return false end end
  return true
end

local function exact(translator, input, segment, minimum)
  if not translator then return false end
  local translation = translator:query(input, segment)
  if not translation then return false end
  local count = 0
  for cand in translation:iter() do
    if cand.start == segment.start and cand._end == segment._end
        and (not minimum or cand.quality >= minimum)
        and (cand.type == 'phrase' or cand.type == 'user_phrase' or cand.type == 'table') then return true end
    count = count + 1
    if count == 8 then break end
  end
  return false
end

local function marked_preedit(input, corrected_preedit, position)
  local letters, changed = {}, nil
  for index = 1, #input do
    local byte = input:sub(index, index)
    if byte:match('[a-z]') then
      letters[#letters + 1] = byte
      if index == position then changed = #letters end
    end
  end
  local result, current = {}, 0
  for index = 1, #corrected_preedit do
    local byte = corrected_preedit:sub(index, index)
    if byte:match('[a-z]') then
      current = current + 1
      if not letters[current] then return input:sub(1, position) .. marker .. input:sub(position + 1) end
      result[#result + 1] = letters[current]
      if current == changed then result[#result + 1] = marker end
    else result[#result + 1] = byte end
  end
  if current ~= #letters then return input:sub(1, position) .. marker .. input:sub(position + 1) end
  return table.concat(result)
end

function M.init(env)
  env.enabled = env.engine.schema.config:get_bool('mason_correction/enabled') == true
  if not env.enabled or not load_syllables() then env.enabled = false; return end
  local function component(namespace, kind)
    return Component.Translator(env.engine, env.engine.schema, namespace, kind)
  end
  local ok, err = pcall(function()
    env.lookup = component('mason_correction_lookup', 'script_translator')
    -- Reuse the read-only component; do not add a second user-dict learner.
    env.original = env.lookup
    env.english = component('melt_eng', 'table_translator')
    env.custom = component('custom_phrase', 'table_translator')
  end)
  if not ok then env.enabled = false; log.error('Mason correction unavailable: ' .. tostring(err)) end
end

local function suggestions(input, segment, env)
  -- Complete legal syllables include normal text and unfinished word prefixes.
  -- Keep them intact instead of marking a valid letter while the user types.
  if legal(input) then return {} end
  -- Abbreviations carry a spelling penalty even when they match a real word.
  -- The read-only lookup has sentence/completion disabled, so full-span lexical
  -- matches are valid without a score threshold; arbitrary sentences are not.
  if exact(env.original, input, segment) or exact(env.custom, input, segment, 90)
      or exact(env.english, input, segment, .65) then return {} end
  local variants = {}
  for position = 1, #input do
    local original = input:sub(position, position)
    for _, neighbor in ipairs(neighbors[original] or {}) do
      local corrected = input:sub(1, position - 1) .. neighbor.letter .. input:sub(position + 1)
      if legal(corrected) then variants[#variants + 1] = {
          input=corrected, position=position, distance=neighbor.distance} end
    end
  end
  table.sort(variants, function(a, b)
    if a.distance ~= b.distance then return a.distance < b.distance end
    if a.position ~= b.position then return a.position > b.position end
    return a.input < b.input
  end)
  local results, seen = {}, {}
  for index = 1, math.min(#variants, 48) do
    local variant = variants[index]
    local translation = env.lookup:query(variant.input, segment)
    local count, group, best_frequency = 0, {}, 0
    if translation then
      for cand in translation:iter() do
        if cand._end == segment._end and cand.type == 'phrase' and cand.quality >= 1 then
          local frequency = math.max(0, cand.quality - 1.2)
          local score = 1.05 + math.min(.1, math.log(1 + frequency * 10000000) * .01) - variant.distance * .015
          best_frequency = math.max(best_frequency, frequency)
          group[#group + 1] = {text=cand.text, quality=score, corrected=variant.input,
            preedit=marked_preedit(input, cand.preedit or variant.input, variant.position),
            position=variant.position, distance=variant.distance}
        end
        count = count + 1
        if count == 6 then break end
      end
    end
    for _, suggestion in ipairs(group) do
      local previous = seen[suggestion.text]
      if not previous or suggestion.quality > previous.quality then seen[suggestion.text] = suggestion end
    end
  end
  for _, suggestion in pairs(seen) do results[#results + 1] = suggestion end
  table.sort(results, function(a, b)
    if a.quality == b.quality then return a.text < b.text end
    return a.quality > b.quality
  end)
  return results
end

function M.func(input, segment, env)
  if not env.enabled or env.engine.context:get_option('ascii_mode') or segment.start ~= 0
      or not segment:has_tag('abc') or #input < 3 or #input > 32
      or not input:match("^[a-z']+$") or input:sub(1, 1) == 'v' or input:sub(-1) == "'" then return end
  if env.cache_input ~= input then
    local ok, result = pcall(suggestions, input, segment, env)
    env.cache_input, env.cache = input, ok and result or {}
    if not ok and not env.reported then
      log.error('Mason correction skipped: ' .. tostring(result)); env.reported = true
    end
  end
  for index = 1, math.min(#env.cache, 12) do
    local value = env.cache[index]
    -- A plain candidate deliberately carries no incorrect Phrase dictionary code.
    local cand = Candidate('mason_correction', segment.start, segment._end, value.text, '')
    cand.quality, cand.preedit = value.quality, value.preedit
    yield(cand)
  end
end
return M
