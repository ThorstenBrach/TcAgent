---
description: Where an object is used and what a change would touch
params:
  - name: target
    ask: Which object?
    type: pou
    default: {object}
    required: true
    help: A POU, GVL or task of this solution
  - name: depth
    ask: How many call levels?
    type: number
    default: 2
  - name: writes
    ask: Only places that write to it
    type: bool
    default: false
  - name: format
    ask: Show the result as
    type: choice
    options: list, table
    default: table
metadata:
  tcagent-category: Understand
  tcagent-tags: references, impact, calls
---
Show where {target} is used, up to {depth} call levels, and what a change to it would touch. Only places that write to it: {writes}. Give the result as a {format}. Change nothing.
