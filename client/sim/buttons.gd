class_name Buttons
## One player's input for one frame, as bit flags in an int (16 bits are sent online).
## Same bits as Buttons.cs in the C# reference.

const NONE := 0
const LEFT := 1 << 0
const RIGHT := 1 << 1
const UP := 1 << 2
const DOWN := 1 << 3
const JUMP := 1 << 4
const LIGHT := 1 << 5
const HEAVY := 1 << 6
const DODGE := 1 << 7
const GRAB := 1 << 8
const DROP_WEAPON := 1 << 9
const TAUNT := 1 << 10

## Every defined button; anything else is masked off before it reaches the sim.
const KNOWN := (1 << 11) - 1
