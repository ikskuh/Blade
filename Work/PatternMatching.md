Plan to refactor the expectation declarations:

We keep the same basic idea, but we have to implement it better:

1. Each `SEQUENCE:` block is independent of each other. This means that two blocks must be evaluated independently.
2. Each `CONTAINS:` block is treated independent of each other. This means that two blocks must be evaluated independently.
3. Remove the old behaviour of `EXACT:`, and implement a new one: It behaves like `SEQUENCE:` but does not allow interleaved unexpected items.

The prefixes `-`, `!` and `(\d+)x` still keep their meaning:

- In `CONTAINS` they assert:
  - `-`: the file contains at least one such item
  - `!`: the file contains no such item
  - `(\d+)x`: the file contains exactly $1 of these items, with $1 >= 1
- In `SEQUENCE:` and `EXACT:`, they assert:
  - `-`: The sequence advances with this item
  - `(\d+)x`: The sequence advances with this item repeated for $1 times, with $1 >= 1
  - `!`: The sequence does not contain this item between the previous and the next non-`!` item.
    - If it's the first item, the file must not contain the item before the first non-`!` item.
    - If it's the last item, the file must not contain the item after the last non-`!` item.
    - If the sequence is composed of only `!` items, the sequence is malformed.

For the pattern matching:

It seems like the pattern matching is broken right now due to two reasons:

1. Whitespace collapse/removal kills several correct patterns.
2. Wrong use of regex backtracking.

As a `?` or `?(\d+)` patterns are basically the same matching algorithm, the numbered version just assert more data.

Examples for broken behavior:

- If all whitespace is removed, `ANDN ?1, #10` and `AND ?1, #20` will potentially match `ANDN FOO,#10` and `AND NFOO, #20`, which are not the same pattern.

How we're going to implement it:

1. Each `CONTAINS:`, `SEQUENCE:` and `EXACT:` block have their own unique placeholder namespace.
   - This allows us matching two or three independent properties while not having to think about number assignments
2. We "compile" the patterns so we get a better matching algorithm:
   1. Split the pattern string at `r"\?(\d+)?"`
   2. For each string inbetween the wildcards, apply the normalization algorithm:
      1. Replace `r"\b"` with `" "`: This will introduce a space between each word and non-word character
         - Reason: We normalize "ANDN#10,20" into " ANDN # 10 , 20 "
      2. Replace `r"\s+"` with `" "`: Normalize all whitespace into a single space character
         - Reason: We normalize "     ANDN     #10 ,     20     " into " ANDN # 10 , 20 "
      3. Strip the leading whitespace for the first split element
      4. Strip the trailing whitespace for the last split element
         - This may be the same element as 3. iff there's no wildcare in the source pattern 
   3. Compile a "Pattern" instance:
      - Pattern is a sequence of N patterns and N+1 normalized string values
      - This means we reinsert the parts we've used to split in step 1. again
      - While merging, we have to wrap the pattern into two space characters
        - This way, we normalize "ANDN#?,20" and "     ANDN     #? ,     20     " into "ANDN # ? , 20"
3. To match, we execute our matching algorithm:
   1. Strip comments from the source code
      - ASMIR and SPIN2 use `'` for comments, do not strip `;`
      - For MIR/LIR/BOUND, strip comments with `;`
   2. Normalize the source lines according to the normalization algorithm
   3. Match the pattern
      - Check if the string starts with the first non-pattern sequence
      - Consume the next identifier (`r"\w+"`) from the string
      - If the pattern is a bound pattern:
        - If the pattern is unbound: Bind the identifier to the pattern.
        - If the pattern is bound: Match the bound value against the identifier. Fail on mismatch.
      - Continue with the next non-pattern sequence, then pattern, ... until the last non-pattern sequence, which isn't followed by a pattern match
      - First and last non-pattern sequence may be an empty string.

This algorithm should be way more robust than what we have right now, and still won't be susceptible to identifier fusion.

Also for `Pattern` implementation:

```cs
sealed class Pattern
{
    public Pattern(string[] sequences, int?[] patterns) // we can encode patterns as just the integer number or null if not bound
    {
        Requires.That(sequences.Length == patterns.Length + 1); // enforce the N+1 sequences for N patterns rule
        Requires.That(sequences[1..^1].All(x => x.Length > 0)); // all interior sequences must be non-empty (they will be a single space for "??" or "?1?2")
    }
}
```
