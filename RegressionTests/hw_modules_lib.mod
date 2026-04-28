
layout Counter {
    cog var counter: u32 = 0;
}

fn increment() : layout(Counter)
{   
    counter = counter + 1;
}