# Shoots Language v0.1

## Grammar
command.id arg=value arg="quoted value"

## Rules
- command.id is required
- args are key=value
- values are strings
- quoting preserves whitespace
- unknown args are ignored
- unknown commands return error

## Reserved Commands
- core.help
- core.commands
- core.command
