const buddy = document.querySelector(".cursor-buddy");

if (buddy && window.matchMedia("(pointer: fine)").matches) {
  let targetX = window.innerWidth / 2;
  let targetY = window.innerHeight / 2;
  let currentX = targetX;
  let currentY = targetY;
  let visible = false;

  const showBuddy = () => {
    visible = true;
    buddy.style.opacity = "1";
  };

  window.addEventListener("pointermove", (event) => {
    targetX = event.clientX + 18;
    targetY = event.clientY + 14;

    if (!visible) {
      currentX = targetX;
      currentY = targetY;
      showBuddy();
    }
  });

  window.addEventListener("pointerleave", () => {
    visible = false;
    buddy.style.opacity = "0";
  });

  const animate = () => {
    currentX += (targetX - currentX) * 0.22;
    currentY += (targetY - currentY) * 0.22;
    buddy.style.transform = `translate3d(${currentX}px, ${currentY}px, 0)`;
    requestAnimationFrame(animate);
  };

  buddy.style.opacity = "0";
  animate();
}
