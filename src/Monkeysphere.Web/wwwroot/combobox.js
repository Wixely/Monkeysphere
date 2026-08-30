document.addEventListener("keydown", event => {
    if (event.target instanceof HTMLInputElement &&
        event.target.classList.contains("combobox-input") &&
        ["ArrowDown", "ArrowUp", "Enter", "Escape"].includes(event.key)) {
        event.preventDefault();
    }
});

document.addEventListener("pointerdown", event => {
    document.querySelectorAll(".combobox:focus-within").forEach(combobox => {
        if (!combobox.contains(event.target)) {
            combobox.querySelector(".combobox-input")?.blur();
        }
    });
});
