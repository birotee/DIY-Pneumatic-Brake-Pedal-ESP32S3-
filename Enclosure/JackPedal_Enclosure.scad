// Units mm. Prototype: board 83x83, Overall assembled enclosure 108x108x50. Internal board clearance 40.
// part: base, lid or layout. Lid prints exterior face down.
part="layout";
module base(){difference(){union(){difference(){translate([0,0,0]) cube([101,101,47]);translate([3,3,3]) cube([95,95,45]);translate([20,-1,12]) cube([62,5,36]);translate([97,20,25]) cube([5,12,23]);}translate([9,12,3]) cube([7,7,4]);translate([9,88,3]) cube([7,7,4]);translate([85,12,3]) cube([7,7,4]);translate([85,88,3]) cube([7,7,4]);}translate([6,14,-1]) cube([2,5,5]);translate([6,90,-1]) cube([2,5,5]);translate([93,14,-1]) cube([2,5,5]);translate([93,90,-1]) cube([2,5,5]);}}
module lid(){difference(){translate([0,0,0]) cube([108,108,11]);translate([3,3,3]) cube([102,102,9]);translate([76,68,-1]) cube([5,2,5]);translate([90,100,-1]) cube([5,2,5]);}}
module lid_complete(){union(){lid();translate([72,72,3]) cube([2,27,3]);translate([97,72,3]) cube([2,27,3]);}}
if(part=="base") base(); else if(part=="lid") lid_complete(); else {base(); translate([120,0,0]) lid_complete();}
