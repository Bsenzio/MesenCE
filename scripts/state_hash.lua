-- Deterministic NES CPU RAM hash for HD/original gameplay-state comparisons.
-- Reads only: it never writes memory and never changes input.
-- Usage with --verify: both passes log one STATEHASH line at the same frame.
local targetFrame = 300
local done = false

local function ringHash()
  local h = 2166136261
  for address = 0, 0x7FF do
    local value = emu.read(address, emu.memType.nesInternalRam)
    h = (h ~ value) * 16777619
    h = h & 0xFFFFFFFF
  end
  return h
end

local function onInputPolled()
  local frame = emu.getState().frameCount or 0
  if not done and frame >= targetFrame then
    done = true
    local line = string.format("STATEHASH %08X frame=%d", ringHash(), frame)
    emu.log(line)
    local file = io.open("D:\\MesenHighWork\\tmp\\statehash.txt", "a")
    if file then
      file:write(line .. "\n")
      file:close()
    end
  end
end

emu.addEventCallback(onInputPolled, emu.eventType.inputPolled)
