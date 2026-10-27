import re

def clean_line_all_occurrences(line):
    # We will find all matches of the pattern in the line
    # Since we want to replace them one by one and update the line, we can do it in a loop
    pattern = re.compile(r'\s*\+\s*\"\\\\n\"\)')
    
    while True:
        match = pattern.search(line)
        if not match:
            break
            
        prefix = line[:match.start()]
        suffix = line[match.end():]
        
        # Count non-string parentheses in the prefix
        in_string = False
        escaped = False
        open_p = 0
        close_p = 0
        
        for i, char in enumerate(prefix):
            if escaped:
                escaped = False
                continue
            if char == '\\':
                escaped = True
                continue
            if char == '"':
                in_string = not in_string
                continue
            if not in_string:
                if char == '(':
                    open_p += 1
                elif char == ')':
                    close_p += 1
                    
        if open_p > close_p:
            line = prefix + ");" + suffix
        else:
            line = prefix + ";" + suffix
            
    return line

with open("Ubar 3.0/MainPage.xaml.cs", "r", encoding="utf-8") as f:
    lines = f.readlines()

new_lines = [clean_line_all_occurrences(line) for line in lines]

with open("Ubar 3.0/MainPage.xaml.cs", "w", encoding="utf-8") as f:
    f.writelines(new_lines)

print("Done cleaning all occurrences in MainPage.xaml.cs")
